using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityRuntimeGuid
{
    [Serializable]
    public class GuidRegistry<T> where T : GuidRegistryEntry
    {
        private Dictionary<int, T> _objectIdToGuidCache;
        private Dictionary<string, T> _guidToEntryCache;

        [SerializeField] private List<T> entries = new();

        private bool IsCacheInitialized => _objectIdToGuidCache != null && _guidToEntryCache != null;

        private void InitializeCache()
        {
            _objectIdToGuidCache = new Dictionary<int, T>();
            _guidToEntryCache = new Dictionary<string, T>();

            // Remove reference to assets that have been deleted at build time (e.g. probuilder meshes)
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i] == null || entries[i].@object == null)
                {
                    entries.RemoveAt(i);
                }
            }

            foreach (var entry in entries)
            {
                _objectIdToGuidCache[entry.@object.GetInstanceID()] = entry;
                _guidToEntryCache[entry.guid] = entry;
            }
        }

        public GuidRegistry<T> Copy()
        {
            var copy = new GuidRegistry<T>
            {
                entries = new List<T>(entries)
            };
            return copy;
        }

        public T GetOrCreateEntry(Object obj, Func<Object, T> createEntryFunc)
        {
            if (TryGetEntry(obj, out var registryEntry)) return registryEntry;
            registryEntry = createEntryFunc(obj);
            TryAdd(registryEntry);
            return registryEntry;
        }

        public void Clear()
        {
            _objectIdToGuidCache = null;
            _guidToEntryCache = null;
            entries.Clear();
        }

        public bool TryAdd(T guidEntry)
        {
            if (guidEntry == null || guidEntry.@object == null)
                return false;

            if (!IsCacheInitialized)
                InitializeCache();

            var added = _objectIdToGuidCache.TryAdd(guidEntry.@object.GetInstanceID(), guidEntry);

            if (added)
            {
                _guidToEntryCache[guidEntry.guid] = guidEntry;
                entries.Add(guidEntry);
            }

            return added;
        }

        public bool TryGetEntry(Object obj, out T entry)
        {
            if (!IsCacheInitialized)
                InitializeCache();

            if (obj != null)
                return _objectIdToGuidCache.TryGetValue(obj.GetInstanceID(), out entry);
            entry = null;
            return false;
        }

        public bool TryGetEntry(string guid, out T entry)
        {
            if (!IsCacheInitialized)
                InitializeCache();
            
            return _guidToEntryCache.TryGetValue(guid, out entry);
        }

        public T GetEntryByGuid(string guid)
        {
            if (!IsCacheInitialized)
                InitializeCache();

            return _guidToEntryCache[guid];
        }

        public bool Remove(Object obj)
        {
            if (!IsCacheInitialized)
                InitializeCache();

            if (!_objectIdToGuidCache.TryGetValue(obj.GetInstanceID(), out var entry))
                return false;
            _guidToEntryCache.Remove(entry.guid);
            _objectIdToGuidCache.Remove(obj.GetInstanceID());
            entries.Remove(entry);
            return true;
        }

        public IReadOnlyList<T> GetAllEntries()
        {
            return entries;
        }
    }
}