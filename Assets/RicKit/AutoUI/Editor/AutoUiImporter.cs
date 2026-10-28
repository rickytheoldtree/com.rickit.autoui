using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AutoUI.Editor
{
    // Builds the visual prefab of a layout. Generated prefabs carry no Auto UI component: which object belongs to which
    // node is kept in the page manifest, so re-import finds the same objects and their references survive.
    public static class AutoUiImporter
    {
        private static readonly Regex SafeId = new Regex("^[a-zA-Z0-9_-]{1,80}$");

        // previousPaths: node ID → object path under the root, as registered when the prefab was last written
        // (null for a new prefab). nodePaths receives the paths to register now.
        internal static GameObject SavePrefab(LayoutDocument layout, string prefabPath, IDictionary<string, Sprite> sprites,
            IDictionary<string, string> previousPaths, IDictionary<string, string> nodePaths,
            TMP_FontAsset fallbackFont = null, IDictionary<string, TMP_FontAsset> fonts = null)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (File.Exists(prefabPath) && existing == null)
                throw new InvalidOperationException("输出位置存在非 Prefab 文件。");
            if (existing != null && previousPaths == null)
                throw new InvalidOperationException("拒绝覆盖未登记的 Prefab：" + prefabPath);
            EnsureFolder(Path.GetDirectoryName(prefabPath).Replace('\\', '/'));
            var root = existing != null ? PrefabUtility.LoadPrefabContents(prefabPath) :
                new GameObject("Visual_" + layout.documentId, typeof(RectTransform));
            try
            {
                var rootRect = (RectTransform)root.transform;
                root.name = string.IsNullOrEmpty(layout.name) ? "Visual_" + layout.documentId : layout.name;
                rootRect.anchorMin = rootRect.anchorMax = new Vector2(0.5f, 0.5f);
                rootRect.pivot = new Vector2(0.5f, 0.5f);
                rootRect.sizeDelta = new Vector2(layout.width, layout.height);
                rootRect.anchoredPosition = Vector2.zero;
                // Resolve every registered path before anything moves.
                var previous = new Dictionary<string, RectTransform>();
                foreach (var pair in previousPaths ?? new Dictionary<string, string>())
                    if (root.transform.Find(pair.Value) is RectTransform found) previous[pair.Key] = found;
                var active = new Dictionary<string, RectTransform>();
                var nodeMap = layout.nodes.ToDictionary(x => x.id);
                var canvasRect = new LayoutRect { width = layout.width, height = layout.height };
                foreach (var node in layout.nodes)
                {
                    var parent = string.IsNullOrEmpty(node.parentId) ? rootRect : active[node.parentId];
                    if (!previous.TryGetValue(node.id, out var target))
                        target = (RectTransform)new GameObject(node.id, typeof(RectTransform)).transform;
                    target.name = string.IsNullOrEmpty(node.name) ? node.id : node.name;
                    target.SetParent(parent, false);
                    target.SetAsLastSibling();
                    target.localScale = Vector3.one;
                    target.localRotation = Quaternion.identity;
                    var parentBounds = string.IsNullOrEmpty(node.parentId) ? canvasRect : nodeMap[node.parentId].rect;
                    SetGeometry(target, node.rect, parentBounds, node.anchor);
                    ApplyComponents(target.gameObject, node, sprites, fallbackFont, fonts);
                    target.gameObject.SetActive(node.visible);
                    active.Add(node.id, target);
                }
                // 先重建所有父子关系，再移除旧节点，避免误删被移动的子节点。
                foreach (var pair in previous)
                    if (!active.ContainsKey(pair.Key) && pair.Value != null)
                        Object.DestroyImmediate(pair.Value.gameObject);
                foreach (var node in layout.nodes.Where(x => x.kind == "button"))
                {
                    var button = active[node.id].GetComponent<Button>();
                    button.targetGraphic = active[node.id].GetComponent<Graphic>();
                    if (button.targetGraphic != null) button.targetGraphic.raycastTarget = true;
                    else
                    {
                        var hitArea = active[node.id].gameObject.AddComponent<Image>();
                        hitArea.color = Color.clear;
                        hitArea.raycastTarget = true;
                        button.targetGraphic = hitArea;
                    }
                    button.transition = Selectable.Transition.None;
                }
                foreach (var pair in active) nodePaths[pair.Key] = PathFrom(root.transform, pair.Value);
                var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath, out var success);
                if (!success) throw new IOException("保存视觉 Prefab 失败。");
                Debug.Log($"AUTO_UI_IMPORTED: {layout.nodes.Length} nodes → {prefabPath}");
                return saved;
            }
            finally
            {
                if (existing != null) PrefabUtility.UnloadPrefabContents(root);
                else Object.DestroyImmediate(root);
            }
        }

        private static string PathFrom(Transform root, Transform target)
        {
            var path = target.name;
            for (var parent = target.parent; parent != null && parent != root; parent = parent.parent) path = parent.name + "/" + path;
            return path;
        }

        public static void SetGeometry(RectTransform target, LayoutRect rect, LayoutRect parent, string anchor)
        {
            target.pivot = new Vector2(0.5f, 0.5f);
            var left = rect.x - parent.x;
            var top = rect.y - parent.y;
            if (anchor == "stretch")
            {
                target.anchorMin = Vector2.zero;
                target.anchorMax = Vector2.one;
                target.offsetMin = new Vector2(left, parent.height - top - rect.height);
                target.offsetMax = new Vector2(left + rect.width - parent.width, -top);
                return;
            }
            var point = new Vector2(0, 1);
            if (anchor == "center") point = new Vector2(0.5f, 0.5f);
            else if (anchor == "topRight") point = Vector2.one;
            else if (anchor == "bottomLeft") point = Vector2.zero;
            else if (anchor == "bottomRight") point = new Vector2(1, 0);
            target.anchorMin = target.anchorMax = point;
            target.sizeDelta = new Vector2(rect.width, rect.height);
            target.anchoredPosition = new Vector2(left + rect.width / 2 - point.x * parent.width,
                -top - rect.height / 2 + (1 - point.y) * parent.height);
        }

        private static void ApplyComponents(GameObject go, LayoutNode node, IDictionary<string, Sprite> sprites,
            TMP_FontAsset fallback, IDictionary<string, TMP_FontAsset> fonts)
        {
            var textNode = node.kind == "text";
            var needsImage = !textNode && !string.IsNullOrEmpty(node.asset);
            if (!needsImage && go.GetComponent<Image>() != null) Object.DestroyImmediate(go.GetComponent<Image>());
            if (!textNode && go.GetComponent<TextMeshProUGUI>() != null) Object.DestroyImmediate(go.GetComponent<TextMeshProUGUI>());
            if (node.kind != "button" && go.GetComponent<Button>() != null) Object.DestroyImmediate(go.GetComponent<Button>());
            if (needsImage)
            {
                var image = go.GetComponent<Image>() ?? go.AddComponent<Image>();
                image.sprite = sprites[node.id];
                image.type = node.border.Any(v => v > 0) ? Image.Type.Sliced : Image.Type.Simple;
                image.color = Color.white;
                image.raycastTarget = node.raycastTarget;
                image.preserveAspect = false;
            }
            if (textNode)
            {
                var text = go.GetComponent<TextMeshProUGUI>() ?? go.AddComponent<TextMeshProUGUI>();
                text.font = ResolveFont(node.text.font, fallback, fonts);
                text.text = node.text.content;
                text.fontSize = node.text.fontSize;
                text.color = node.text.color;
                text.enableAutoSizing = false;
                text.richText = false;
                text.margin = Vector4.zero;
                text.raycastTarget = node.raycastTarget;
                text.alignment = node.text.alignment == "center" ? TextAlignmentOptions.Center :
                    node.text.alignment == "right" ? TextAlignmentOptions.Right : TextAlignmentOptions.Left;
            }
            if (node.kind == "button" && go.GetComponent<Button>() == null) go.AddComponent<Button>();
        }

        private static TMP_FontAsset ResolveFont(string name, TMP_FontAsset fallback, IDictionary<string, TMP_FontAsset> fonts)
        {
            return name != null && fonts != null && fonts.TryGetValue(name, out var font) && font != null ? font : fallback;
        }

        internal static void Validate(LayoutDocument layout, string sourceRoot, bool acceptReview,
            TMP_FontAsset fallback, IDictionary<string, TMP_FontAsset> fonts)
        {
            if (layout == null || layout.schemaVersion != 1 || layout.origin != "topLeft" || layout.units != "px" ||
                layout.colorSpace != "sRGB" || layout.drawOrder != "backToFront" || !IsId(layout.documentId) ||
                layout.width <= 0 || layout.height <= 0 || (long)layout.width * layout.height > 32000000 ||
                layout.nodes == null || layout.nodes.Length == 0 || layout.nodes.Length > 20000)
                throw new InvalidDataException("不支持的布局格式、画布或 documentId。");
            if (layout.reviewRequired && !acceptReview)
                throw new InvalidOperationException("此导出包存在效果差异或待检查项。请先查看 report.html，然后勾选已检查。");
            var ids = new HashSet<string>();
            foreach (var node in layout.nodes)
            {
                if (!IsId(node.id) || !ids.Add(node.id) ||
                    (!string.IsNullOrEmpty(node.parentId) && !ids.Contains(node.parentId)) || node.parentId == node.id)
                    throw new InvalidDataException("节点 ID 重复，或父节点未排在子节点之前。");
                if (!new[] { "group", "image", "button", "text" }.Contains(node.kind) || node.rect == null ||
                    !Finite(node.rect.x) || !Finite(node.rect.y) || !Finite(node.rect.width) || !Finite(node.rect.height) ||
                    node.rect.width < 0 || node.rect.height < 0 || node.rect.width > 30000 || node.rect.height > 30000)
                    throw new InvalidDataException("节点类型或矩形无效：" + node.id);
                if (!new[] { "topLeft", "topRight", "bottomLeft", "bottomRight", "center", "stretch" }.Contains(node.anchor))
                    throw new InvalidDataException("未知锚点：" + node.id);
                if (node.border == null || node.border.Length != 4 || node.border.Any(v => !Finite(v) || v < 0))
                    throw new InvalidDataException("九宫格边距无效：" + node.id);
                if (node.kind == "text" && (node.text == null || !Finite(node.text.fontSize) || node.text.fontSize <= 0 ||
                                            ResolveFont(node.text.font, fallback, fonts) == null))
                    throw new InvalidDataException("动态文字需要有效文字信息与 TMP 字体映射：" + node.id);
                if (!string.IsNullOrEmpty(node.asset))
                {
                    var file = ResolveAsset(sourceRoot, node.asset);
                    if (!File.Exists(file)) throw new FileNotFoundException("缺少切图", file);
                    var header = new byte[24];
                    using (var stream = File.OpenRead(file))
                        if (stream.Read(header, 0, header.Length) != header.Length) throw new InvalidDataException("PNG 不完整。");
                    var signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
                    if (!header.Take(8).SequenceEqual(signature)) throw new InvalidDataException("资源不是 PNG。");
                    var width = ReadBigEndian(header, 16);
                    var height = ReadBigEndian(header, 20);
                    if (width != node.rect.width || height != node.rect.height || width > 16384 || height > 16384 ||
                        node.border[0] + node.border[2] > width || node.border[1] + node.border[3] > height)
                        throw new InvalidDataException("PNG 尺寸或九宫格与布局不符：" + node.id);
                }
                else if (node.kind == "image") throw new InvalidDataException("图片节点没有切图：" + node.id);
            }
        }

        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static bool IsId(string value) => value != null && SafeId.IsMatch(value);
        private static uint ReadBigEndian(byte[] bytes, int index) =>
            ((uint)bytes[index] << 24) | ((uint)bytes[index + 1] << 16) | ((uint)bytes[index + 2] << 8) | bytes[index + 3];

        internal static string ResolveAsset(string root, string relative)
        {
            if (!relative.StartsWith("sprites/", StringComparison.Ordinal) || relative.Contains("\\") ||
                relative.Contains(":") || relative.Split('/').Any(p => p == ".." || p == ".") ||
                Path.GetExtension(relative) != ".png") throw new InvalidDataException("非法切图路径：" + relative);
            var result = Path.GetFullPath(Path.Combine(root, relative));
            if (!result.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("切图路径超出导出包。");
            return result;
        }

        internal static string Hash(byte[] bytes)
        {
            using (var algorithm = SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
        }

        internal static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
