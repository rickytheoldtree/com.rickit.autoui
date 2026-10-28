using System;
using UnityEngine;

namespace AutoUI.Editor
{
    [Serializable]
    public sealed class LayoutDocument
    {
        public int schemaVersion;
        public string documentId;
        public string name;
        public int width;
        public int height;
        public string origin;
        public string units;
        public string colorSpace;
        public string drawOrder;
        public bool reviewRequired;
        public LayoutNode[] nodes;
    }

    [Serializable]
    public sealed class LayoutNode
    {
        public string id;
        public string parentId;
        public string name;
        public string sourceName;
        public string kind;
        public bool visible;
        public bool raycastTarget;
        public LayoutRect rect;
        public LayoutRect layoutRect;
        public string asset;
        public string anchor;
        public float[] border;
        public LayoutText text;
    }

    [Serializable]
    public sealed class LayoutRect
    {
        public float x;
        public float y;
        public float width;
        public float height;
    }

    [Serializable]
    public sealed class LayoutText
    {
        public string content;
        public string font;
        public float fontSize = 24;
        public Color color = Color.white;
        public string alignment;
    }
}
