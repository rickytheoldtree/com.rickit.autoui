using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AutoUI.Editor
{
    // Applies an import plan made by `auto-ui plan`. Every input the plan was based on is re-checked before
    // anything is written, and only files registered in the page manifest are ever modified.
    public static class AutoUiPlanImporter
    {
        // -batchmode -executeMethod AutoUI.Editor.AutoUiPlanImporter.ApplyFromCommandLine -autoUiPlan <path> [-autoUiAcceptReview]
        public static void ApplyFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(args, "-autoUiPlan");
            try
            {
                if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("缺少 -autoUiPlan <计划路径>。");
                var result = Apply(args[index + 1], args.Contains("-autoUiAcceptReview"));
                EditorApplication.Exit(result.Value<bool>("ok") ? 0 : 2);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        // For a project whose editor is open, where batchmode cannot run.
        [MenuItem("Tools/Auto UI/执行导入计划…")]
        private static void ApplyFromMenu()
        {
            var path = EditorUtility.OpenFilePanel("选择 import-plan.json", "", "json");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                var package = JObject.Parse(File.ReadAllText(path)).Value<string>("package");
                var review = JObject.Parse(File.ReadAllText(Path.Combine(package, "layout.json"))).Value<bool>("reviewRequired");
                if (review && !EditorUtility.DisplayDialog("待检查的效果", "导出包仍有待检查的效果差异。已查看 report.html 并接受吗？", "接受并导入", "取消"))
                    return;
                var result = Apply(path, review);
                var failed = result["checks"].Where(check => !check.Value<bool>("ok"))
                    .Select(check => check.Value<string>("method") + "：" + check.Value<string>("message")).ToArray();
                EditorUtility.DisplayDialog("Auto UI", failed.Length == 0 ? "导入完成，项目校验全部通过。" :
                    "导入完成，但以下项目校验未通过：\n" + string.Join("\n", failed), "确定");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Auto UI 导入未完成", exception.Message, "确定");
            }
        }

        public static JObject Apply(string planPath, bool acceptReview = false)
        {
            planPath = Path.GetFullPath(planPath);
            var plan = JObject.Parse(File.ReadAllText(planPath));
            var project = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            Require(plan.Value<int>("schemaVersion") == 1, "不支持的导入计划版本。");
            Require(SamePath(plan.Value<string>("project"), project), "该计划属于另一个 Unity 项目。");
            Require(plan["summary"].Value<int>("errors") == 0, "计划包含错误，请先处理 import-plan.html 中的错误并重新生成计划。");
            var package = plan.Value<string>("package");
            var layoutPath = Path.Combine(package, "layout.json");
            var inputs = plan["inputs"];
            Require(FileHash(layoutPath) == inputs.Value<string>("layoutSha256"), "导出包在生成计划后已变化，请重新生成计划。");
            Require(FileHash(plan.Value<string>("profile")) == inputs.Value<string>("profileSha256"), "项目档案在生成计划后已变化，请重新生成计划。");
            var manifestPath = plan.Value<string>("manifest");
            var manifestHash = File.Exists(manifestPath) ? FileHash(manifestPath) : null;
            Require(manifestHash == inputs.Value<string>("manifestSha256"), "页面登记表在生成计划后已变化，请重新生成计划。");
            var layout = JsonConvert.DeserializeObject<LayoutDocument>(File.ReadAllText(layoutPath));
            AutoUiImporter.Validate(layout, package, acceptReview, null, null);
            Require(layout.documentId == plan.Value<string>("documentId"), "计划与导出包身份不一致。");

            var assets = plan["assets"].Cast<JObject>().ToArray();
            foreach (var asset in assets) VerifyUnchanged(asset, package);
            var deletions = (plan["delete"] as JArray ?? new JArray()).Cast<JObject>().ToArray();
            foreach (var deletion in deletions)
            {
                var path = deletion.Value<string>("path");
                Require(File.Exists(path) && FileHash(path) == deletion.Value<string>("projectFileSha256"), "待删除的文件在生成计划后已变化：" + path);
                var guid = deletion.Value<string>("guid");
                Require(string.IsNullOrEmpty(guid) || AssetDatabase.AssetPathToGUID(path) == guid, "待删除文件的 GUID 与登记不一致：" + path);
            }

            var settings = (JObject)plan["textureImport"] ?? new JObject();
            // A sprites-only plan (reskinning an existing panel) has no prefab.
            var prefabPath = plan.Value<string>("prefab");
            var variantFolder = string.IsNullOrEmpty(prefabPath) ? null : Path.GetDirectoryName(prefabPath).Replace('\\', '/') + "/SpriteVariants";
            var sprites = new Dictionary<string, Sprite>();
            var registered = new List<JObject>();
            foreach (var asset in assets)
            {
                var action = asset.Value<string>("action");
                var path = asset.Value<string>("path");
                if (action == "create" || action == "update")
                {
                    AutoUiImporter.EnsureFolder(Path.GetDirectoryName(path).Replace('\\', '/'));
                    File.WriteAllBytes(path, File.ReadAllBytes(AutoUiImporter.ResolveAsset(package, asset.Value<string>("source"))));
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
                    Configure((TextureImporter)AssetImporter.GetAtPath(path), asset, settings, action == "create");
                }
                var renameTo = asset.Value<string>("renameTo");
                if (!string.IsNullOrEmpty(renameTo))
                {
                    // Moving keeps the GUID, so references from other prefabs stay intact.
                    var error = AssetDatabase.MoveAsset(path, renameTo);
                    Require(string.IsNullOrEmpty(error), "重命名失败：" + path + " → " + renameTo + "：" + error);
                    path = renameTo;
                }
                var baseSprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
                Require(baseSprite != null, "无法加载 Sprite：" + path);
                foreach (var nodeId in asset["nodeIds"].Values<string>())
                    sprites[nodeId] = SpriteFor(baseSprite, asset["borders"][nodeId].Values<float>().ToArray(), variantFolder, registered);
                if (action != "reuse")
                    registered.Add(new JObject { ["path"] = path, ["guid"] = AssetDatabase.AssetPathToGUID(path),
                        ["contentSha256"] = asset["contentSha256"], ["nodeIds"] = asset["nodeIds"] });
            }
            var previousManifest = File.Exists(manifestPath) ? JObject.Parse(File.ReadAllText(manifestPath)) : new JObject();
            var nodePaths = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(prefabPath))
            {
                var registeredPrefab = previousManifest["prefab"] as JObject;
                var previousPaths = registeredPrefab?.Value<string>("path") == prefabPath
                    ? ((previousManifest["nodes"] as JObject)?.ToObject<Dictionary<string, string>>() ?? new Dictionary<string, string>())
                    : null;
                AutoUiImporter.SavePrefab(layout, prefabPath, sprites, previousPaths, nodePaths);
            }
            foreach (var deletion in deletions)
                Require(AssetDatabase.DeleteAsset(deletion.Value<string>("path")), "删除失败：" + deletion.Value<string>("path"));
            WriteManifest(plan, manifestPath, prefabPath, registered, nodePaths);
            AssetDatabase.SaveAssets();

            var result = new JObject { ["planId"] = plan["planId"], ["prefab"] = prefabPath, ["deleted"] = deletions.Length, ["ok"] = true };
            var checks = new JArray();
            foreach (var method in plan["postImport"].Values<string>().Concat(plan["validators"].Values<string>()))
            {
                var check = new JObject { ["method"] = method };
                try
                {
                    Invoke(method);
                    check["ok"] = true;
                }
                catch (Exception exception)
                {
                    var inner = exception is TargetInvocationException && exception.InnerException != null ? exception.InnerException : exception;
                    check["ok"] = false;
                    check["message"] = inner.Message;
                    result["ok"] = false;
                }
                checks.Add(check);
            }
            result["checks"] = checks;
            File.WriteAllText(Path.ChangeExtension(planPath, ".result.json"), result.ToString(Formatting.Indented));
            Debug.Log($"AUTO_UI_PLAN_APPLIED: {assets.Length} assets → {prefabPath}; checks ok={result["ok"]}");
            return result;
        }

        private static void VerifyUnchanged(JObject asset, string package)
        {
            var path = asset.Value<string>("path");
            var source = AutoUiImporter.ResolveAsset(package, asset.Value<string>("source"));
            Require(FileHash(source) == asset.Value<string>("sourceSha256"), "导出包切图已变化：" + asset.Value<string>("source"));
            Require(path.StartsWith("Assets/", StringComparison.Ordinal) && !path.Split('/').Contains(".."), "非法项目路径：" + path);
            switch (asset.Value<string>("action"))
            {
                case "create":
                    Require(!File.Exists(path), "目标位置已有文件，请重新生成计划：" + path);
                    break;
                case "keep":
                    var target = asset.Value<string>("renameTo");
                    Require(string.IsNullOrEmpty(target) || !File.Exists(target), "重命名目标已有文件，请重新生成计划：" + target);
                    goto case "update";
                case "update":
                case "reuse":
                    Require(File.Exists(path) && FileHash(path) == asset.Value<string>("projectFileSha256"), "项目素材在生成计划后已变化：" + path);
                    var guid = asset.Value<string>("guid");
                    Require(string.IsNullOrEmpty(guid) || AssetDatabase.AssetPathToGUID(path) == guid, "项目素材 GUID 与计划不一致：" + path);
                    break;
                default:
                    throw new InvalidDataException("未知素材动作：" + asset.Value<string>("action"));
            }
        }

        private static void Configure(TextureImporter importer, JObject asset, JObject settings, bool created)
        {
            // An updated file keeps the import settings project tools have applied since, such as platform compression.
            if (created)
            {
                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = settings.Value<float?>("pixelsPerUnit") ?? 100;
                importer.spritePivot = new Vector2(0.5f, 0.5f);
                importer.mipmapEnabled = false;
                importer.isReadable = false;
                importer.alphaIsTransparency = true;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.sRGBTexture = true;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.wrapMode = TextureWrapMode.Clamp;
                var size = Mathf.Max(asset.Value<int>("width"), asset.Value<int>("height"));
                importer.maxTextureSize = Mathf.Clamp(Mathf.NextPowerOfTwo(size), 32, 16384);
                var textureSettings = new TextureImporterSettings();
                importer.ReadTextureSettings(textureSettings);
                textureSettings.spriteMeshType = SpriteMeshType.FullRect;
                importer.SetTextureSettings(textureSettings);
            }
            var border = asset["importBorder"]?.Type == JTokenType.Array ? asset["importBorder"].Values<float>().ToArray() : new float[4];
            importer.spriteBorder = new Vector4(border[0], border[3], border[2], border[1]);
            importer.SaveAndReimport();
        }

        // A node whose nine-slice differs from the texture's own gets a Sprite asset sharing the same texture.
        // Without a nine-slice the Image draws Simple and ignores the sprite's border, so the sprite is used as is.
        private static Sprite SpriteFor(Sprite baseSprite, float[] border, string folder, List<JObject> registered)
        {
            var expected = new Vector4(border[0], border[3], border[2], border[1]);
            if (expected == Vector4.zero || baseSprite.border == expected) return baseSprite;
            Require(folder != null, "只导入素材的计划不能生成九宫格变体，请在页面配置中去掉该节点的 border 或生成视觉 Prefab。");
            // sprite.texture may already be a packed atlas page; the source PNG is the single-sprite texture.
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(AssetDatabase.GetAssetPath(baseSprite));
            Require(texture != null, "无法加载九宫格变体的源纹理：" + AssetDatabase.GetAssetPath(baseSprite));
            var key = AutoUiImporter.Hash(System.Text.Encoding.UTF8.GetBytes(
                AssetDatabase.GetAssetPath(baseSprite) + ":" + string.Join(",", border)));
            var path = folder + "/spr_" + key.Substring(0, 24) + ".asset";
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(path);
            if (sprite == null)
            {
                AutoUiImporter.EnsureFolder(folder);
                sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f),
                    baseSprite.pixelsPerUnit, 0, SpriteMeshType.FullRect, expected, false);
                sprite.name = Path.GetFileNameWithoutExtension(path);
                AssetDatabase.CreateAsset(sprite, path);
            }
            else Require(sprite.texture == texture && sprite.border == expected, "已存在的 Sprite 变体与资源不匹配：" + path);
            if (registered.All(entry => entry.Value<string>("path") != path))
                registered.Add(new JObject { ["path"] = path, ["guid"] = AssetDatabase.AssetPathToGUID(path), ["variant"] = true });
            return sprite;
        }

        private static void WriteManifest(JObject plan, string manifestPath, string prefabPath, List<JObject> registered,
            IDictionary<string, string> nodePaths)
        {
            var previous = File.Exists(manifestPath) ? JObject.Parse(File.ReadAllText(manifestPath)) : new JObject();
            var paths = new HashSet<string>(registered.Select(entry => entry.Value<string>("path")));
            // Files no longer used stay registered so a later plan can still report or update them.
            var unused = (previous["assets"] as JArray ?? new JArray()).Cast<JObject>()
                .Where(entry => !paths.Contains(entry.Value<string>("path")) && File.Exists(entry.Value<string>("path")))
                .Select(entry => { entry["unused"] = true; return entry; });
            var manifest = new JObject
            {
                ["schemaVersion"] = 1,
                ["page"] = plan["page"],
                ["documentId"] = plan["documentId"],
                ["planId"] = plan["planId"],
                ["prefab"] = string.IsNullOrEmpty(prefabPath) ? null : new JObject { ["path"] = prefabPath, ["guid"] = AssetDatabase.AssetPathToGUID(prefabPath) },
                ["assets"] = new JArray(registered.Concat(unused)),
                // Node ID → object path in the prefab; re-import uses it to keep the same objects.
                ["nodes"] = JObject.FromObject(nodePaths)
            };
            AutoUiImporter.EnsureFolder(Path.GetDirectoryName(manifestPath).Replace('\\', '/'));
            File.WriteAllText(manifestPath, manifest.ToString(Formatting.Indented));
            AssetDatabase.ImportAsset(manifestPath);
        }

        private static void Invoke(string fullName)
        {
            var split = fullName.LastIndexOf('.');
            var typeName = fullName.Substring(0, split);
            var type = AppDomain.CurrentDomain.GetAssemblies().Select(assembly => assembly.GetType(typeName)).FirstOrDefault(t => t != null);
            var method = type?.GetMethod(fullName.Substring(split + 1), BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            Require(method != null, "找不到项目方法：" + fullName);
            method.Invoke(null, null);
        }

        private static string FileHash(string path) => File.Exists(path) ? AutoUiImporter.Hash(File.ReadAllBytes(path)) : null;
        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
