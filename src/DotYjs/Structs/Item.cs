namespace DotYjs;

/// <summary>
/// An item in the linked list of a shared type. This is the core CRDT element (YATA).
/// </summary>
public sealed class Item : AbstractStruct
{
    private const int BitKeep = 1;
    private const int BitCountable = 2;
    private const int BitDeleted = 4;
    private const int BitMarker = 8;

    private int _info;

    /// <summary>Creates an item; it becomes part of the document when it is integrated by a transaction.</summary>
    public Item(ID id, Item? left, ID? origin, Item? right, ID? rightOrigin, object? parent, string? parentSub, AbstractContent content)
        : base(id, content.GetLength())
    {
        Origin = origin;
        Left = left;
        Right = right;
        RightOrigin = rightOrigin;
        Parent = parent;
        ParentSub = parentSub;
        Redone = null;
        Content = content;
        _info = content.IsCountable ? BitCountable : 0;
    }

    /// <summary>Origin is the ID of the item that was to the left when this item was created.</summary>
    public ID? Origin { get; internal set; }

    /// <summary>The item to the left (null at the start of the parent).</summary>
    public Item? Left { get; internal set; }
    /// <summary>The item to the right (null at the end of the parent).</summary>
    public Item? Right { get; internal set; }

    /// <summary>The ID of the item that was to the right when this item was created.</summary>
    public ID? RightOrigin { get; internal set; }

    /// <summary>The parent: an <see cref="AbstractType"/> once integrated, or an <see cref="ID"/>/string while decoding.</summary>
    public object? Parent { get; internal set; }

    /// <summary>If this item is a value in a map-like type, the key.</summary>
    public string? ParentSub { get; internal set; }

    /// <summary>If this item was redone by the UndoManager, the ID of the new item.</summary>
    public ID? Redone { get; internal set; }

    /// <summary>The content of this item.</summary>
    public AbstractContent Content { get; internal set; }

    internal bool Marker
    {
        get => (_info & BitMarker) > 0;
        set { if (Marker != value) _info ^= BitMarker; }
    }

    /// <summary>If true, do not garbage collect this item.</summary>
    public bool Keep
    {
        get => (_info & BitKeep) > 0;
        internal set { if (Keep != value) _info ^= BitKeep; }
    }

    /// <summary>Whether the content counts towards the length of the parent type.</summary>
    public bool Countable => (_info & BitCountable) > 0;

    /// <inheritdoc />
    public override bool Deleted => (_info & BitDeleted) > 0;

    internal void SetDeleted(bool value)
    {
        if (Deleted != value) _info ^= BitDeleted;
    }

    internal void MarkDeleted() => _info |= BitDeleted;

    internal AbstractType ParentType => (AbstractType)Parent!;

    /// <summary>The next non-deleted item.</summary>
    public Item? Next
    {
        get
        {
            var n = Right;
            while (n != null && n.Deleted) n = n.Right;
            return n;
        }
    }

    /// <summary>The previous non-deleted item.</summary>
    public Item? Prev
    {
        get
        {
            var n = Left;
            while (n != null && n.Deleted) n = n.Left;
            return n;
        }
    }

    /// <summary>ID of the last element covered by this item.</summary>
    public ID LastId => Length == 1 ? Id : new ID(Id.Client, Id.Clock + Length - 1);

    internal override long? GetMissing(Transaction transaction, StructStore store)
    {
        if (Origin != null && Origin.Client != Id.Client && Origin.Clock >= store.GetState(Origin.Client)) return Origin.Client;
        if (RightOrigin != null && RightOrigin.Client != Id.Client && RightOrigin.Clock >= store.GetState(RightOrigin.Client)) return RightOrigin.Client;
        if (Parent is ID pid && Id.Client != pid.Client && pid.Clock >= store.GetState(pid.Client)) return pid.Client;

        // all dependencies are available: resolve left/right/parent
        if (Origin != null)
        {
            var leftStruct = store.GetItemCleanEnd(transaction, Origin);
            Left = leftStruct as Item;
            Origin = leftStruct is Item li ? li.LastId : new ID(leftStruct.Id.Client, leftStruct.Id.Clock + leftStruct.Length - 1);
            LeftStruct = leftStruct;
        }
        if (RightOrigin != null)
        {
            var r = store.GetItemCleanStart(transaction, RightOrigin);
            RightStruct = r;
            Right = r as Item;
            RightOrigin = r.Id;
        }
        if (LeftStruct is GC || RightStruct is GC)
        {
            Parent = null;
        }
        else if (Parent == null)
        {
            if (Left != null)
            {
                Parent = Left.Parent;
                ParentSub = Left.ParentSub;
            }
            else if (Right != null)
            {
                Parent = Right.Parent;
                ParentSub = Right.ParentSub;
            }
        }
        else if (Parent is ID parentId)
        {
            // the parent may have been garbage collected in the meantime: either replaced by a GC struct or
            // its content replaced by ContentDeleted. In both cases this item is integrated as GC (parent = null).
            var parentItem = store.Find(parentId);
            Parent = parentItem is Item { Content: ContentType ct } ? ct.Type : null;
        }
        return null;
    }

    // While resolving dependencies, left/right may point to GC structs. These hold the raw struct.
    internal AbstractStruct? LeftStruct;
    internal AbstractStruct? RightStruct;

    internal override void Integrate(Transaction transaction, long offset)
    {
        if (offset > 0)
        {
            Id.Clock += offset;
            var leftStruct = transaction.Doc.Store.GetItemCleanEnd(transaction, new ID(Id.Client, Id.Clock - 1));
            Left = leftStruct as Item;
            LeftStruct = leftStruct;
            Origin = leftStruct is Item li ? li.LastId : new ID(leftStruct.Id.Client, leftStruct.Id.Clock + leftStruct.Length - 1);
            Content = Content.Splice(offset);
            Length -= offset;
        }
        LeftStruct = null;
        RightStruct = null;

        if (Parent is AbstractType parent)
        {
            if ((Left == null && (Right == null || Right.Left != null)) || (Left != null && Left.Right != Right))
            {
                var left = Left;
                Item? o;
                // set o to the first conflicting item
                if (left != null)
                {
                    o = left.Right;
                }
                else if (ParentSub != null)
                {
                    o = parent.Map.GetValueOrDefault(ParentSub);
                    while (o != null && o.Left != null) o = o.Left;
                }
                else
                {
                    o = parent.Start;
                }
                var conflictingItems = new HashSet<Item>(ReferenceEqualityComparer.Instance);
                var itemsBeforeOrigin = new HashSet<Item>(ReferenceEqualityComparer.Instance);
                // Let c in conflictingItems, b in itemsBeforeOrigin
                // ***{origin}bbbb{this}{c,b}{c,b}{o}***
                // Note that conflictingItems is a subset of itemsBeforeOrigin
                while (o != null && o != Right)
                {
                    itemsBeforeOrigin.Add(o);
                    conflictingItems.Add(o);
                    if (ID.Compare(Origin, o.Origin))
                    {
                        // case 1
                        if (o.Id.Client < Id.Client)
                        {
                            left = o;
                            conflictingItems.Clear();
                        }
                        else if (ID.Compare(RightOrigin, o.RightOrigin))
                        {
                            // this and o are conflicting and point to the same integration points. The id decides which item comes first.
                            break;
                        }
                    }
                    else if (o.Origin != null && transaction.Doc.Store.Find(o.Origin) is Item oo && itemsBeforeOrigin.Contains(oo))
                    {
                        // case 2
                        if (!conflictingItems.Contains(oo))
                        {
                            left = o;
                            conflictingItems.Clear();
                        }
                    }
                    else
                    {
                        break;
                    }
                    o = o.Right;
                }
                Left = left;
            }
            // reconnect left/right + update parent map/start if necessary
            if (Left != null)
            {
                var right = Left.Right;
                Right = right;
                Left.Right = this;
            }
            else
            {
                Item? r;
                if (ParentSub != null)
                {
                    r = parent.Map.GetValueOrDefault(ParentSub);
                    while (r != null && r.Left != null) r = r.Left;
                }
                else
                {
                    r = parent.Start;
                    parent.Start = this;
                }
                Right = r;
            }
            if (Right != null)
            {
                Right.Left = this;
            }
            else if (ParentSub != null)
            {
                // set as current parent value if right === null and this is parentSub
                parent.Map[ParentSub] = this;
                if (Left != null)
                {
                    // this is the current attribute value of parent. delete right
                    Left.Delete(transaction);
                }
            }
            // adjust length of parent
            if (ParentSub == null && Countable && !Deleted)
            {
                parent.LengthInternal += Length;
            }
            transaction.Doc.Store.AddStruct(this);
            Content.Integrate(transaction, this);
            // add parent to transaction.changed
            transaction.AddChangedType(parent, ParentSub);
            if ((parent.Item != null && parent.Item.Deleted) || (ParentSub != null && Right != null))
            {
                // delete if parent is deleted or if this is not the current attribute value of parent
                Delete(transaction);
            }
        }
        else
        {
            // parent is not defined. Integrate GC struct instead
            new GC(Id, Length).Integrate(transaction, 0);
        }
    }

    internal override bool MergeWith(AbstractStruct rightStruct)
    {
        if (rightStruct is Item right &&
            ID.Compare(right.Origin, LastId) &&
            Right == right &&
            ID.Compare(RightOrigin, right.RightOrigin) &&
            Id.Client == right.Id.Client &&
            Id.Clock + Length == right.Id.Clock &&
            Deleted == right.Deleted &&
            Redone == null &&
            right.Redone == null &&
            Content.GetType() == right.Content.GetType() &&
            Content.MergeWith(right.Content))
        {
            var searchMarker = (Parent as AbstractType)?.SearchMarker;
            if (searchMarker != null)
            {
                foreach (var marker in searchMarker)
                {
                    if (marker.P == right)
                    {
                        // right is going to be "forgotten" so we need to update the marker
                        marker.P = this;
                        // adjust marker index
                        if (!Deleted && Countable) marker.Index -= Length;
                    }
                }
            }
            if (right.Keep) Keep = true;
            Right = right.Right;
            if (Right != null) Right.Left = this;
            Length += right.Length;
            return true;
        }
        return false;
    }

    /// <summary>Mark this item as deleted.</summary>
    internal void Delete(Transaction transaction)
    {
        if (!Deleted)
        {
            var parent = (AbstractType)Parent!;
            // adjust the length of parent
            if (Countable && ParentSub == null) parent.LengthInternal -= Length;
            MarkDeleted();
            transaction.DeleteSet.Add(Id.Client, Id.Clock, Length);
            transaction.AddChangedType(parent, ParentSub);
            Content.Delete(transaction);
        }
    }

    internal void Gc(StructStore store, bool parentGCd)
    {
        if (!Deleted) throw new InvalidOperationException("Unexpected case");
        Content.Gc(store);
        if (parentGCd) store.ReplaceStruct(this, new GC(Id, Length));
        else Content = new ContentDeleted(Length);
    }

    internal override void Write(IUpdateEncoder encoder, long offset)
    {
        var origin = offset > 0 ? new ID(Id.Client, Id.Clock + offset - 1) : Origin;
        var rightOrigin = RightOrigin;
        var parentSub = ParentSub;
        int info = (Content.Ref & 0x1f) |
            (origin == null ? 0 : 0x80) |
            (rightOrigin == null ? 0 : 0x40) |
            (parentSub == null ? 0 : 0x20);
        encoder.WriteInfo((byte)info);
        if (origin != null) encoder.WriteLeftID(origin);
        if (rightOrigin != null) encoder.WriteRightID(rightOrigin);
        if (origin == null && rightOrigin == null)
        {
            switch (Parent)
            {
                case AbstractType parent:
                    {
                        var parentItem = parent.Item;
                        if (parentItem == null)
                        {
                            // parent type on y._map: find the correct key
                            var ykey = ID.FindRootTypeKey(parent);
                            encoder.WriteParentInfo(true);
                            encoder.WriteString(ykey);
                        }
                        else
                        {
                            encoder.WriteParentInfo(false);
                            encoder.WriteLeftID(parentItem.Id);
                        }
                        break;
                    }
                case string s:
                    encoder.WriteParentInfo(true);
                    encoder.WriteString(s);
                    break;
                case ID pid:
                    encoder.WriteParentInfo(false);
                    encoder.WriteLeftID(pid);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected case");
            }
            if (parentSub != null) encoder.WriteString(parentSub);
        }
        Content.Write(encoder, offset);
    }

    // ---- helpers used by the UndoManager & RelativePosition ----

    /// <summary>Follows the redone chain of an ID (yjs followRedone).</summary>
    internal static (AbstractStruct item, long diff) FollowRedone(StructStore store, ID id)
    {
        ID? nextID = id;
        long diff = 0;
        AbstractStruct item;
        do
        {
            if (diff > 0) nextID = new ID(nextID!.Client, nextID.Clock + diff);
            item = store.Find(nextID!);
            diff = nextID!.Clock - item.Id.Clock;
            nextID = (item as Item)?.Redone;
        } while (nextID != null && item is Item);
        return (item, diff);
    }

    /// <summary>Make sure that neither item nor any of its parents is ever deleted (garbage collected).</summary>
    internal static void KeepItem(Item? item, bool keep)
    {
        while (item != null && item.Keep != keep)
        {
            item.Keep = keep;
            item = ((AbstractType)item.Parent!).Item;
        }
    }

    /// <summary>Split <paramref name="leftItem"/> into two items at <paramref name="diff"/>.</summary>
    internal static Item SplitItem(Transaction transaction, Item leftItem, long diff)
    {
        var client = leftItem.Id.Client;
        var clock = leftItem.Id.Clock;
        var rightItem = new Item(
            new ID(client, clock + diff),
            leftItem,
            new ID(client, clock + diff - 1),
            leftItem.Right,
            leftItem.RightOrigin,
            leftItem.Parent,
            leftItem.ParentSub,
            leftItem.Content.Splice(diff));
        if (leftItem.Deleted) rightItem.MarkDeleted();
        if (leftItem.Keep) rightItem.Keep = true;
        if (leftItem.Redone != null) rightItem.Redone = new ID(leftItem.Redone.Client, leftItem.Redone.Clock + diff);
        // update left (do not set leftItem.rightOrigin as it will lead to problems when syncing)
        leftItem.Right = rightItem;
        // update right
        if (rightItem.Right != null) rightItem.Right.Left = rightItem;
        // right is more specific.
        transaction.MergeStructs.Add(rightItem);
        // update parent._map
        if (rightItem.ParentSub != null && rightItem.Right == null && rightItem.Parent is AbstractType pt)
        {
            pt.Map[rightItem.ParentSub] = rightItem;
        }
        leftItem.Length = diff;
        return rightItem;
    }

    /// <summary>Check if <paramref name="parent"/> is a (transitive) parent of <paramref name="child"/> (yjs isParentOf).</summary>
    internal static bool IsParentOf(AbstractType parent, Item? child)
    {
        while (child != null)
        {
            if (ReferenceEquals(child.Parent, parent)) return true;
            child = ((AbstractType)child.Parent!).Item;
        }
        return false;
    }

    private static bool IsDeletedByUndoStack(List<StackItem> stack, ID id)
    {
        foreach (var s in stack)
        {
            if (s.Deletions.IsDeleted(id)) return true;
        }
        return false;
    }

    /// <summary>Redoes the effect of this operation (yjs redoItem). Returns the redone item or null if it could not be redone.</summary>
    internal static Item? RedoItem(Transaction transaction, Item item, HashSet<Item> redoitems, DeleteSet itemsToDelete, bool ignoreRemoteMapChanges, UndoManager um)
    {
        var doc = transaction.Doc;
        var store = doc.Store;
        var ownClientID = doc.ClientID;
        var redone = item.Redone;
        if (redone != null) return (Item)store.GetItemCleanStart(transaction, redone);
        var parentItem = ((AbstractType)item.Parent!).Item;
        Item? left = null;
        Item? right;
        // make sure that parent is redone
        if (parentItem != null && parentItem.Deleted)
        {
            // try to undo parent if it will be undone anyway
            if (parentItem.Redone == null && (!redoitems.Contains(parentItem) || RedoItem(transaction, parentItem, redoitems, itemsToDelete, ignoreRemoteMapChanges, um) == null))
            {
                return null;
            }
            while (parentItem.Redone != null)
            {
                parentItem = (Item)store.GetItemCleanStart(transaction, parentItem.Redone);
            }
        }
        var parentType = parentItem == null ? (AbstractType)item.Parent! : ((ContentType)parentItem.Content).Type;

        if (item.ParentSub == null)
        {
            // Is an array item. Insert at the old position
            left = item.Left;
            right = item;
            // find next cloned_redo items
            while (left != null)
            {
                var leftTrace = left;
                // trace redone until parent matches
                while (leftTrace != null && !ReferenceEquals(((AbstractType)leftTrace.Parent!).Item, parentItem))
                {
                    leftTrace = leftTrace.Redone == null ? null : (Item)store.GetItemCleanStart(transaction, leftTrace.Redone);
                }
                if (leftTrace != null && ReferenceEquals(((AbstractType)leftTrace.Parent!).Item, parentItem))
                {
                    left = leftTrace;
                    break;
                }
                left = left.Left;
            }
            while (right != null)
            {
                var rightTrace = right;
                // trace redone until parent matches
                while (rightTrace != null && !ReferenceEquals(((AbstractType)rightTrace.Parent!).Item, parentItem))
                {
                    rightTrace = rightTrace.Redone == null ? null : (Item)store.GetItemCleanStart(transaction, rightTrace.Redone);
                }
                if (rightTrace != null && ReferenceEquals(((AbstractType)rightTrace.Parent!).Item, parentItem))
                {
                    right = rightTrace;
                    break;
                }
                right = right.Right;
            }
        }
        else
        {
            right = null;
            if (item.Right != null && !ignoreRemoteMapChanges)
            {
                left = item;
                // Iterate right while right is in itemsToDelete
                // If it is intended to delete right while item is redone, we can expect that item should replace right.
                while (left != null && left.Right != null && (left.Right.Redone != null || itemsToDelete.IsDeleted(left.Right.Id) || IsDeletedByUndoStack(um.UndoStack, left.Right.Id) || IsDeletedByUndoStack(um.RedoStack, left.Right.Id)))
                {
                    left = left.Right;
                    // follow redone
                    while (left.Redone != null) left = (Item)store.GetItemCleanStart(transaction, left.Redone);
                }
                if (left != null && left.Right != null)
                {
                    // It is not possible to redo this item because it conflicts with a
                    // change from another client
                    return null;
                }
            }
            else
            {
                left = parentType.Map.TryGetValue(item.ParentSub, out var l) ? l : null;
            }
        }
        var nextClock = store.GetState(ownClientID);
        var nextId = new ID(ownClientID, nextClock);
        var redoneItem = new Item(
            nextId,
            left, left?.LastId,
            right, right?.Id,
            parentType,
            item.ParentSub,
            item.Content.Copy());
        item.Redone = nextId;
        KeepItem(redoneItem, true);
        redoneItem.Integrate(transaction, 0);
        return redoneItem;
    }

    /// <inheritdoc />
    public override string ToString() => $"Item{Id} {Content.GetType().Name}";
}
