// Copyright © Clinton Ingram and Contributors
// SPDX-License-Identifier: MIT

using System;
using System.Diagnostics.CodeAnalysis;

namespace PhotoSauce.MagicScaler;

internal interface IMultiDisposable : IDisposable
{
	bool TryAddRef();
}

internal sealed class SimpleLruCache<TKey, TValue> where TKey : IEquatable<TKey> where TValue : IMultiDisposable
{
	private const int maxItems = 8;

	private sealed class CacheNode
	{
		public readonly TKey Key;
		public readonly TValue Value;

		public CacheNode? Prev;
		public CacheNode? Next;

		public CacheNode(TKey key, TValue value) => (Key, Value) = (key, value);
	}

	private readonly object sync = new();

	private CacheNode? head;
	private CacheNode? tail;
	private volatile int count;

	private bool tryGetInternal(TKey key, [NotNullWhen(true)] out TValue? value)
	{
		for (var curr = head; curr is not null; curr = curr.Next)
		{
			if (!curr.Key.Equals(key))
				continue;

			bool keep = curr.Value.TryAddRef();

			var prev = curr.Prev;
			var next = curr.Next;

			if (prev is not null)
			{
				prev.Next = next;

				if (next is null)
					tail = prev;
				else
					next.Prev = prev;

				if (keep)
				{
					head!.Prev = curr;
					curr.Next = head;
					curr.Prev = null;

					head = curr;
				}
			}

			if (keep)
			{
				value = curr.Value;
				return true;
			}

			if (prev is null)
			{
				if (next is not null)
					next.Prev = null;

				head = next;
			}

			count--;
		}

		value = default;
		return false;
	}

	public bool TryGet(TKey key, [NotNullWhen(true)] out TValue? value)
	{
		lock (sync)
		{
			return tryGetInternal(key, out value);
		}
	}

	public TValue GetOrAdd(TKey key, TValue value)
	{
		lock (sync)
		{
			if (tryGetInternal(key, out var existing))
			{
				value.Dispose();

				return existing;
			}

			if (value.TryAddRef())
			{
				var node = new CacheNode(key, value);

				node.Next = head;
				if (head is not null)
					head.Prev = node;

				head = node;
				tail ??= node;

				count++;
			}

			for (; count > maxItems; count--)
			{
				var last = tail!.Prev!;
				last.Next = null;

				tail.Value.Dispose();
				tail = last;
			}
		}

		return value;
	}
}
