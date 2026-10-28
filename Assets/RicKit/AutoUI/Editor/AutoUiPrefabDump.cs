using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AutoUI.Editor
{
    // Dumps an existing UI prefab as data so it can be compared with an imported design: hierarchy, canvas-space
    // rects, images, texts, nested prefabs and every object reference held by scripts.
    public static class AutoUiPrefabDump
    {
        // -batchmode -executeMethod AutoUI.Editor.AutoUiPrefabDump.DumpFromCommandLine -autoUiPrefab <a.prefab;b.prefab> -autoUiOut <json or directory> [-autoUiCanvas 1080x1920]
        public static void DumpFromCommandLine()
        {
            try
            {
                var canvas = AutoUiPreview.CanvasArgument();
                if (canvas == Vector2Int.zero) canvas = new Vector2Int(1080, 1920);
                // Several prefabs separated by ';' are written as <name>.json into the -autoUiOut directory.
                var prefabs = AutoUiPreview.Argument("-autoUiPrefab").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                var output = AutoUiPreview.Argument("-autoUiOut");
                if (prefabs.Length > 1) Directory.CreateDirectory(output);
                foreach (var prefab in prefabs)
                    File.WriteAllText(prefabs.Length > 1 ? Path.Combine(output, Path.GetFileNameWithoutExtension(prefab) + ".json") : output,
                        Dump(prefab, canvas).ToString(Formatting.Indented));
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        public static JObject Dump(string prefabPath, Vector2Int canvasSize)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) throw new InvalidOperationException("找不到 Prefab：" + prefabPath);
            var canvasObject = new GameObject("AutoUiDumpCanvas", typeof(RectTransform), typeof(Canvas));
            try
            {
                var canvasRect = (RectTransform)canvasObject.transform;
                canvasRect.sizeDelta = canvasSize;
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, canvasRect);
                AutoUiPreview.Fit(instance.transform as RectTransform);
                Canvas.ForceUpdateCanvases();
                var nodes = new JArray();
                Walk(instance.transform, "", canvasRect, nodes);
                return new JObject { ["prefab"] = prefabPath, ["canvas"] = new JArray(canvasSize.x, canvasSize.y), ["nodes"] = nodes };
            }
            finally
            {
                Object.DestroyImmediate(canvasObject);
            }
        }

        private static void Walk(Transform transform, string parent, RectTransform canvas, JArray nodes)
        {
            var path = string.IsNullOrEmpty(parent) ? transform.name : parent + "/" + transform.name;
            var node = new JObject { ["path"] = path, ["active"] = transform.gameObject.activeSelf };
            if (transform is RectTransform rect)
            {
                // Canvas space, top-left origin, matching layout.json.
                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                var min = canvas.InverseTransformPoint(corners[0]);
                var max = canvas.InverseTransformPoint(corners[2]);
                var size = canvas.rect.size;
                node["rect"] = new JObject
                {
                    ["x"] = Math.Round(min.x + size.x / 2, 1), ["y"] = Math.Round(size.y / 2 - max.y, 1),
                    ["width"] = Math.Round(max.x - min.x, 1), ["height"] = Math.Round(max.y - min.y, 1)
                };
                // Authored values, needed to change a layout without breaking how it adapts to other screens.
                node["rectTransform"] = new JObject
                {
                    ["anchorMin"] = Vec(rect.anchorMin), ["anchorMax"] = Vec(rect.anchorMax), ["pivot"] = Vec(rect.pivot),
                    ["anchoredPosition"] = Vec(rect.anchoredPosition), ["sizeDelta"] = Vec(rect.sizeDelta),
                    ["scale"] = Vec(rect.localScale)
                };
            }
            if (PrefabUtility.IsAnyPrefabInstanceRoot(transform.gameObject))
                node["prefab"] = AssetDatabase.GetAssetPath(PrefabUtility.GetCorrespondingObjectFromSource(transform.gameObject));
            var components = new JArray();
            foreach (var component in transform.GetComponents<Component>())
            {
                if (component == null)
                {
                    components.Add(new JObject { ["type"] = "MissingScript" });
                    continue;
                }
                if (component is Transform || component is CanvasRenderer) continue;
                var entry = new JObject { ["type"] = component.GetType().Name };
                if (component is Image image)
                {
                    entry["sprite"] = Reference(image.sprite);
                    entry["imageType"] = image.type.ToString();
                    entry["color"] = "#" + ColorUtility.ToHtmlStringRGBA(image.color);
                    entry["raycastTarget"] = image.raycastTarget;
                }
                else if (component is TMP_Text text)
                {
                    entry["text"] = text.text;
                    entry["font"] = Reference(text.font);
                    entry["material"] = Reference(text.fontSharedMaterial);
                    entry["fontSize"] = text.fontSize;
                    entry["color"] = "#" + ColorUtility.ToHtmlStringRGBA(text.color);
                }
                else if (component is MonoBehaviour && !(component is Graphic))
                {
                    // Layout components and scripts: value settings plus object references.
                    entry["values"] = Values(component);
                    if (!(component is UIBehaviour)) entry["fields"] = Fields(component, transform.root);
                }
                components.Add(entry);
            }
            node["components"] = components;
            nodes.Add(node);
            foreach (Transform child in transform) Walk(child, path, canvas, nodes);
        }

        // Object references held by a script: scene objects as hierarchy paths, assets as project paths.
        private static JObject Fields(Component component, Transform root)
        {
            var fields = new JObject();
            var property = new SerializedObject(component).GetIterator();
            while (property.NextVisible(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference || property.name == "m_Script") continue;
                var value = property.objectReferenceValue;
                fields[property.propertyPath] = value == null ? null : Reference(value, root);
            }
            return fields;
        }

        private static JObject Values(Component component)
        {
            var values = new JObject();
            var property = new SerializedObject(component).GetIterator();
            while (property.NextVisible(true))
            {
                switch (property.propertyType)
                {
                    case SerializedPropertyType.Integer: values[property.propertyPath] = property.intValue; break;
                    case SerializedPropertyType.Boolean: values[property.propertyPath] = property.boolValue; break;
                    case SerializedPropertyType.Float: values[property.propertyPath] = Math.Round(property.floatValue, 3); break;
                    case SerializedPropertyType.String: values[property.propertyPath] = property.stringValue; break;
                    case SerializedPropertyType.Enum:
                        var index = property.enumValueIndex;
                        values[property.propertyPath] = index >= 0 && index < property.enumDisplayNames.Length ? property.enumDisplayNames[index] : property.intValue.ToString();
                        break;
                    case SerializedPropertyType.Vector2: values[property.propertyPath] = Vec(property.vector2Value); break;
                }
            }
            return values;
        }

        private static JArray Vec(Vector2 value) => new JArray(Math.Round(value.x, 2), Math.Round(value.y, 2));
        private static JArray Vec(Vector3 value) => new JArray(Math.Round(value.x, 3), Math.Round(value.y, 3), Math.Round(value.z, 3));

        private static JToken Reference(Object value, Transform root = null)
        {
            if (value == null) return null;
            var asset = AssetDatabase.GetAssetPath(value);
            if (!string.IsNullOrEmpty(asset)) return value is Sprite ? asset + "#" + value.name : asset;
            var transform = value is Component component ? component.transform : (value as GameObject)?.transform;
            if (transform == null) return value.name;
            var path = transform.name;
            for (var parent = transform.parent; parent != null && parent != root?.parent; parent = parent.parent) path = parent.name + "/" + path;
            return path + (value is Component ? ":" + value.GetType().Name : "");
        }
    }
}
