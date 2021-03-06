﻿using System;
using System.Collections.Generic;
using VRage;

namespace Equinox.Utils.Cache
{
    public class LruCache<TK, TV> : CacheBase<TK,TV>
    {
        private struct CacheItem
        {
            public TK key;
            public TV value;
        }

        private readonly Dictionary<TK, LinkedListNode<CacheItem>> cache;
        private readonly LinkedList<CacheItem> lruCache;
        private readonly int capacity;
        private readonly FastResourceLock m_lock;

        public LruCache(int capacity, IEqualityComparer<TK> comparer)
        {
            this.cache = new Dictionary<TK, LinkedListNode<CacheItem>>(capacity > 1024 ? (int)Math.Sqrt(capacity) : capacity, comparer ?? EqualityComparer<TK>.Default);
            this.lruCache = new LinkedList<CacheItem>();
            this.capacity = capacity;
            this.m_lock = new FastResourceLock();
        }
        
        public override TV GetOrCreate(TK key, CreateDelegate del)
        {
            using (m_lock.AcquireExclusiveUsing())
            {
                TV res;
                if (TryGetUnsafe(key, out res)) return res;
                if (cache.Count >= capacity)
                    while (cache.Count >= capacity / 1.5)
                    {
                        cache.Remove(lruCache.First.Value.key);
                        lruCache.RemoveFirst();
                    }

                var node = new LinkedListNode<CacheItem>(new CacheItem() { key = key, value = del(key) });
                lruCache.AddLast(node);
                cache.Add(key, node);
                return node.Value.value;
            }
        }

        public override void Clear()
        {
            using (m_lock.AcquireExclusiveUsing())
            {
                this.cache.Clear();
                this.lruCache.Clear();
            }
        }

        public override TV Store(TK key, TV value)
        {
            using (m_lock.AcquireExclusiveUsing())
            {
                var node = new LinkedListNode<CacheItem>(new CacheItem() {key = key, value = value});
                lruCache.AddLast(node);
                cache[key] = node;
                return node.Value.value;
            }
        }

        private bool TryGetUnsafe(TK key, out TV value)
        {
            LinkedListNode<CacheItem> node;
            if (cache.TryGetValue(key, out node))
            {
                lruCache.Remove(node);
                lruCache.AddLast(node);
                value = node.Value.value;
                return true;
            }
            value = default(TV);
            return false;
        }

        public override bool TryGet(TK key, out TV value)
        {
            using (m_lock.AcquireExclusiveUsing())
                return TryGetUnsafe(key, out value);
        }
    }
}
