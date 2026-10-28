# RicKit Auto UI：给 AI 的约定

在 Unity 项目里导入 Auto UI 导出包、或按设计稿修改现有界面前先读这份文档。完整流程在 `auto-ui-import` skill 里；这里只说本包的工具和规则。

## 不要绕过工具

- 素材落位、复用、改名、删除都通过导入计划完成：Python 端 `AutoUI.exe plan` 生成计划，本包执行。不要手动复制 PNG、手改 `.meta` 或登记表。
- 本包只修改页面登记表（`<page>.json`）里登记过的文件；目标位置已有未登记的文件或 Prefab 时会拒绝执行。
- 计划记录了导出包、项目档案、登记表和每个项目素材的哈希。生成计划后任何一项变化，执行都会拒绝；重新生成计划即可。

## 执行

- 编辑器关着：`-batchmode -executeMethod AutoUI.Editor.AutoUiPlanImporter.ApplyFromCommandLine -autoUiPlan <计划>`，导出包有待检查的效果且用户已确认时加 `-autoUiAcceptReview`。
- 编辑器开着：请用户点菜单 **Tools → Auto UI → 执行导入计划…**。
- 结果在 `<计划同名>.result.json`：`ok`、删除数量、档案里 `postImport`/`validators` 每一项的结果。失败时读日志里的异常。

## 生成的视觉 Prefab

- 只含 uGUI 组件（RectTransform、Image、Button、TextMeshProUGUI），不依赖本包。
- 节点与对象的对应关系在登记表的 `nodes` 里（节点 ID → 对象路径）。重新导入按它找回对象：fileID 不变，改名的节点原地改名。不要手动改视觉 Prefab 的层级，否则下次导入找不到对象会新建。
- 业务部分放在外层面板预制体里，嵌套视觉 Prefab；脚本用序列化字段引用节点，在编辑器脚本里按登记表的路径找到对象后赋值。

## 读现有界面、核对结果

- `AutoUiPrefabDump`：导出现有预制体的层级、画布坐标、锚点与尺寸设置、布局组件参数、图片、文字、嵌套预制体和脚本字段引用。换皮前先读它，不要逐行读预制体 YAML。
- `AutoUiPreview`：在不透明背景上渲染 Prefab；全屏拉伸的面板加 `-autoUiCanvas 1080x1920`。与组装包的 `preview.png` 背景相同，可直接比较。图集压缩带来零散单像素差异属正常，成片差异要查原因。

## 版本

本包版本与 `AutoUI.exe` 版本一致。项目 `Packages/manifest.json` 里的版本比安装的工具旧时，提醒用户升级（改版本号即可）。
