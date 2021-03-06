﻿#region Usings

using System.Collections.Generic;
using System.Linq;
using A2B.Annotations;
using RimWorld;
using Verse;
using Verse.Sound;

#endregion

namespace A2B
{
    public class ThingStatus
    {
        public ThingStatus([NotNull] Thing thing, int counter)
        {
            Thing = thing;
            Counter = counter;
        }

        [NotNull]
        public Thing Thing { get; private set; }

        public int Counter { get; private set; }
    }

    public class BeltItemContainer : Saveable, ThingContainerGiver
    {
        private readonly BeltComponent _parentComponent;

        private readonly Dictionary<Thing, int> _thingCounter;

        private ThingContainer _container;

        public BeltItemContainer([NotNull] BeltComponent component)
        {
            _parentComponent = component;

            _container = new ThingContainer(this);
            _thingCounter = new Dictionary<Thing, int>();
        }

        [NotNull]
        public IEnumerable<Thing> Contents
        {
            get { return _container.Contents; }
        }

        [NotNull]
        public IEnumerable<Thing> ThingsToMove
        {
            get { return _thingCounter.Where(pair => pair.Value >= _parentComponent.BeltSpeed).Select(pair => pair.Key).ToList(); }
        }

        public bool WorkToDo
        {
            get { return _thingCounter.Any(pair => pair.Value >= _parentComponent.BeltSpeed); }
        }

        public bool Empty
        {
            get { return _container.Empty; }
        }

        [NotNull]
        public IEnumerable<ThingStatus> ThingStatus
        {
            get { return _container.Contents.Select(thing => new ThingStatus(thing, _thingCounter[thing])); }
        }

        #region Saveable Members

        public void ExposeData()
        {
            Scribe_Deep.LookDeep(ref _container, "container");

            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Dictionary<string, int> counterDictionary = null;
                Scribe_Fixed.LookDictionary(ref counterDictionary, "thingCounter", LookMode.Value);

                _thingCounter.Clear();
                if (counterDictionary != null)
                {
                    foreach (var pair in counterDictionary)
                    {
                        var thing = _container.Contents.FirstOrDefault(t => t.ThingID == pair.Key);

                        if (thing != null)
                        {
                            _thingCounter.Add(thing, pair.Value);
                        }
                    }
                }
            }
            else if (Scribe.mode == LoadSaveMode.Saving)
            {
                var counterDictionary = _thingCounter.ToDictionary(pair => pair.Key.ThingID, pair => pair.Value);

                Scribe_Fixed.LookDictionary(ref counterDictionary, "thingCounter", LookMode.Value);
            }
        }

        #endregion

        #region ThingContainerGiver Members

        [NotNull]
        ThingContainer ThingContainerGiver.GetContainer()
        {
            return _container;
        }

        #endregion

        public void Tick()
        {
            _container.ThingContainerTick();

            foreach (var thing in Contents.Where(ShouldIncreaseCounter))
            {
                _thingCounter[thing]++;
            }
        }

        private bool ShouldIncreaseCounter([NotNull] Thing thing)
        {
            var currentCounter = _thingCounter[thing];
            if (currentCounter < _parentComponent.BeltSpeed / 2 && !_parentComponent.IsReceiver())
            {
                // Always increase the counter until half the belt speed is reached
                return true;
            }

            // Never go above 100%
            if (currentCounter >= _parentComponent.BeltSpeed)
            {
                return false;
            }

            var destination = _parentComponent.GetDestinationForThing(thing);

            var belt = destination.GetBeltComponent();

            // If no belt items, then move things only if this can output to non-belts
            if (belt == null)
            {
                return (_parentComponent.CanOutputToNonBelt() && destination.CanPlaceThing(thing));
            }

            // If there is a belt, only move things if it can accept them from us
            return belt.CanAcceptFrom(_parentComponent);
        }

        public bool AddItem([NotNull] Thing t, int initialCounter = 0)
        {
            if (!_container.TryAdd(t))
            {
                return false;
            }

            _thingCounter[t] = initialCounter;
            return true;
        }

        public void TransferItem([NotNull] Thing item, [NotNull] BeltItemContainer other)
        {
            _container.Remove(item);
            _thingCounter.Remove(item);

            other.AddItem(item);

            _parentComponent.OnItemTransfer(item, other._parentComponent);
        }

        public void DropItem([NotNull] Thing item, IntVec3 position, bool forced = false)
        {
            var backupSound = item.def.soundDrop;

            try
            {
                item.def.soundDrop = null;

                if (!_container.Contains(item))
                    return;

                Thing droppedItem;
                if (!_container.TryDrop(item, position, ThingPlaceMode.Direct, out droppedItem) && !forced)
                {       
                    return;
                }
                // Might prevent those "has null owner" errors...
                else if (forced && _container.Contains(item) && !_container.TryDrop(item, position, ThingPlaceMode.Near, out droppedItem))
                {
                    _container.Remove(item);
                    item.holder = null;
                    return;
                }

                // Play the sound as that isn't handled by the ThingContainer anymore...
                if (backupSound != null)
                {
                    backupSound.PlayOneShot(position);
                }

                _thingCounter.Remove(item);
                item.holder = null;

                if (droppedItem is ThingWithComps)
                {
                    droppedItem.SetForbidden(false);
                }

                if (droppedItem.def.defName.Contains("Chunk") && Find.DesignationManager.DesignationOn(droppedItem, DesignationDefOf.Haul) == null)
                {
                    // If this is a chunk AND not already haulable ("designated twice" warning) make it haulable
                    Find.DesignationManager.AddDesignation(new Designation(droppedItem, DesignationDefOf.Haul));
                }
            }
            finally
            {
                // Stupid hack to make sure the drop sound is not played all the time
                item.def.soundDrop = backupSound;
            }
        }

        public void DropAll(IntVec3 position, bool forced = false)
        {
            // Check if there is anything on the belt: yes? -> make it accessible to colonists
            foreach (var thing in _container.Contents.ToList())
            {
                DropItem(thing, position, forced);
            }

            _thingCounter.Clear();
        }

        public void Destroy()
        {
            DropAll(_parentComponent.parent.Position, true);
            _container.DestroyContents();
        }
    }
}
