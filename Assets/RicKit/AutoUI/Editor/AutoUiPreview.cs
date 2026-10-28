using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AutoUI.Editor
{
    // Renders a UI prefab to PNG, so an import can be compared with the source design.
    // The background is opaque: UI blending squares alpha on a transparent target, which would fade glows and shadows.
    public static class AutoUiPreview
    {
        // Same backdrop as the composed package's preview.png.
        public static readonly Color DefaultBackground = new Color32(40, 47, 60, 255);

        // -batchmode -executeMethod AutoUI.Editor.AutoUiPreview.RenderFromCommandLine -autoUiPrefab <Assets/...prefab> -autoUiOut <png> [-autoUiCanvas 1080x1920]
        // Without -autoUiCanvas the prefab's own root size is used; full-screen panels need the design canvas.
        public static void RenderFromCommandLine()
        {
            try
            {
                Render(Argument("-autoUiPrefab"), Argument("-autoUiOut"), CanvasArgument(), DefaultBackground);
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        public static void Render(string prefabPath, string output) => Render(prefabPath, output, Vector2Int.zero, DefaultBackground);

        public static void Render(string prefabPath, string output, Vector2Int canvasSize, Color background)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null || !(prefab.transform is RectTransform rect)) throw new InvalidOperationException("不是 UI Prefab：" + prefabPath);
            var width = canvasSize.x > 0 ? canvasSize.x : Mathf.RoundToInt(rect.rect.width);
            var height = canvasSize.y > 0 ? canvasSize.y : Mathf.RoundToInt(rect.rect.height);
            if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                throw new InvalidOperationException("Prefab 根节点尺寸无效，全屏面板请用 -autoUiCanvas 指定画布：" + prefabPath);
            var cameraObject = new GameObject("AutoUiPreviewCamera", typeof(Camera));
            var canvasObject = new GameObject("AutoUiPreviewCanvas", typeof(RectTransform), typeof(Canvas));
            var texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var previous = RenderTexture.active;
            try
            {
                var camera = cameraObject.GetComponent<Camera>();
                camera.transform.position = new Vector3(0, 0, -10);
                camera.orthographic = true;
                camera.orthographicSize = height / 2f;
                camera.aspect = (float)width / height;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = background;
                var canvas = canvasObject.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.worldCamera = camera;
                ((RectTransform)canvas.transform).sizeDelta = new Vector2(width, height);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, canvas.transform);
                Fit((RectTransform)instance.transform);
                texture.Create();
                camera.targetTexture = texture;
                Canvas.ForceUpdateCanvases();
                camera.Render();
                RenderTexture.active = texture;
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                pixels.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
                File.WriteAllBytes(output, pixels.EncodeToPNG());
                Debug.Log($"AUTO_UI_PREVIEW: {prefabPath} → {output}");
            }
            finally
            {
                RenderTexture.active = previous;
                texture.Release();
                Object.DestroyImmediate(pixels);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(canvasObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        // A stretched root fills the canvas as it would under the game's canvas; a fixed-size root is centred.
        internal static void Fit(RectTransform rect)
        {
            if (rect.anchorMin != rect.anchorMax)
            {
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                return;
            }
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
        }

        internal static Vector2Int CanvasArgument()
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "-autoUiCanvas");
            if (index < 0) return Vector2Int.zero;
            var parts = index + 1 < args.Length ? args[index + 1].Split('x') : new string[0];
            if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height))
                throw new ArgumentException("-autoUiCanvas 格式为 宽x高，例如 1080x1920。");
            return new Vector2Int(width, height);
        }

        internal static string Argument(string name)
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, name);
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("缺少参数 " + name);
            return args[index + 1];
        }
    }
}
