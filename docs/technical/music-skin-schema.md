# 原生谱面皮肤 schema 1 与资源限额

安装目录为 `data/music-skins/<id>/`。列表发现只读取每包有大小上限的 `skin.json` 元数据，不打开、哈希或解码纹理；列入列表不代表资源已通过完整验证。实际异步加载调用 `MusicSkinPackageValidator.ValidateDirectory`，验证整包后再由 WPF 解码。未来导入器可复用同一验证器检查暂存目录。

## 本地 ZIP 导入布局

设置页只接受用户选择的 `.zip` 文件，不提供下载或商店。ZIP 可采用以下任一布局：

1. 皮肤内容直接位于 ZIP 根目录，根目录包含 `skin.json`。
2. ZIP 根目录恰有一个 `<id>/` 包装目录，清单位于 `<id>/skin.json`；包装目录名必须与清单 `id` 一致。

不支持额外的外层目录、多个皮肤、大小写不敏感的重复路径、绝对或盘符路径、跨磁盘分卷 ZIP、符号链接/重解析点，也不接受 JSON、PNG、JPG、JPEG 以外的文件。ZIP 文件自身最多为解压总量上限再加 16 MiB；中央目录也限制为 16 MiB，且条目数在创建 `ZipArchive` 前按文件和目录上限检查。中央目录偏移加大小必须恰好到达 EOCD 或 ZIP64 EOCD，二者之间存在空隙或声明不一致时会被拒绝，以确保按实际归档范围执行目录大小限额。带有歧义结束目录签名的 ZIP 注释会被拒绝。导入器先将数据流式解压至皮肤目录内的临时目录，对每个文件和包总量按实际读取字节执行上限，再调用 `MusicSkinPackageValidator.ValidateDirectory` 完整验证；通过后才以目录移动方式安装到 `data/music-skins/<id>/`。已安装的同 ID 皮肤会被拒绝，不覆盖原包。取消或失败会清除暂存目录；若安装成功后暂存清理因文件系统错误失败，状态会明确显示皮肤已安装及残留暂存目录。

## 最小有效皮肤

创建目录 `data/music-skins/example-minimal/`，仅放以下 `skin.json` 即可得到无外部纹理的矢量皮肤：

```json
{
  "schemaVersion": 1,
  "id": "example-minimal",
  "displayName": "Minimal native skin",
  "capabilities": ["native-v1"],
  "contentHashes": {}
}
```

以上五个字段均必填。`id` 与目录名须一致（比较时忽略大小写），长 1–128 字符，以英文字母或数字开头，后续只允许英文字母、数字、`_`、`-`；不允许 Windows 设备名，`default` 保留给内置皮肤。`displayName` 不能为空。当前唯一支持的能力声明是恰好 `["native-v1"]`，代表原生 WPF 数据皮肤，不包含可执行代码。

可选元数据包括 `author`、`version`、`description`、`preview`。`colors` 仅支持 `#RGB`、`#ARGB`、`#RRGGBB`、`#AARRGGBB`（十六进制大小写均可）和 WPF `Colors` 表中的命名色（忽略大小写，如 `Red`、`Transparent`）；允许首尾空白。未提供的颜色采用内置配色；已提供但不符合格式的颜色会使清单验证失败，并指出字段。`ContextColor`、`scRGB`、URI 和其他扩展语法均不支持；解析器不调用 WPF `ColorConverter`，也不加载外部 ICC 色彩配置文件。公开颜色辅助方法遇到非法值只返回调用者提供的备用颜色。资源路径必须相对当前包，禁止绝对路径、`..`、ADS 流、符号链接与目录联接。包内只允许 JSON、PNG、JPG、JPEG 文件。

## 纹理和内容哈希

`textures` 可包含 `noteTap`、`noteHold`、`notePoint`、`background`、`backgroundGradient`、`backgroundArch`、`backgroundWing`、`holdBeam`。例如 `"textures": { "notePoint": "images/note-point.png" }` 是显式包内引用；未声明 `textures.notePoint` 时不会查找其他包或隐式加载同名图片。

`effects` 可包含 `hitFlower`、`hitShockwave`、`hitSpark`、`hitRay` 图片引用，以及 `hitEffectDurationMs`、`hitEffectScale`。`preview` 同样是图片引用。所有声明的图片必须存在且有效；任一资源失败时整个加载返回默认皮肤与原因。

在 `contentHashes` 中列出包内**除根 `skin.json` 外的每个文件**，包含未引用图片和其他 JSON。键为包内相对路径（推荐 `/`），值为该文件原始字节的 SHA-256，恰好 64 个十六进制字符，大小写均可。不得包含根 `skin.json`、不存在的文件或规范化后重复的路径。修改文件后必须同步更新哈希。验证器检查全包哈希，解码器在读取图片的同一锁定文件流上再次验证。哈希用于完整性检查，不代表发布者身份认证。

## 默认限额

| 项目 | 上限或范围 |
| --- | --- |
| 包内文件数（含 `skin.json`） | 512 |
| 包内子目录数 | 512 |
| 包总文件字节 | 256 MiB |
| 单张压缩图片字节 | 32 MiB |
| 每个 JSON 文件 | 1 MiB，解析深度 32 |
| 原图宽、高 | 各 4096 像素 |
| 包内原图像素总和 | 64 × 1024 × 1024 像素 |
| 共享账本存活解码条目 | 512 |
| 共享账本解码像素估算 | 128 MiB |
| `hitEffectDurationMs` | 有限数值 1–2000 ms；默认 280 |
| `hitEffectScale` | 有限数值 0.1–4；默认 1 |

图片签名、头部及原图尺寸先于 WPF 解码检查。设置预览采用 **960×540** 解码目标上限；浮层按游戏客户区的实际物理像素边界（DIP × DPI / 96）取值，最多 **1920×1080**，窗口尺寸变化后重新请求匹配尺寸。小纹理、特效和封面还限制到 512×512。解码保持宽高比且不放大原图。这些是图片解码目标上限，不限制浮层坐标、窗口大小或 DPI 缩放。

## 所有权、诊断和重试

缓存仅持有 `WeakReference<BitmapSource>`，按规范路径、内容哈希和解码尺寸共享。所有仍存活的位图按**宽 × 高 × 8 字节**保守估算，串行准入同时包括正在解码的预留额度。默认仓储共享账本；测试或专用调用显式传入限额时使用独立账本。

该数值不是进程 Private Bytes 或 GPU 显存，也不包含所有 WIC 临时分配与 WPF 合成开销。清空视图、切换皮肤或调用缓存 `Clear()` 不等于实际内存已回收；只在弱引用确认对象不再存活后移除对应账目。外部引用仍持有图片时持续计费。生产代码不强制 GC；即便所有消费者都已放弃强引用，GC 尚未回收时仍可能保守拒绝新解码，并返回“预算不足，请关闭其他预览后稍候重试”的明确提示。

设置 VM、浮层 Presenter 和控件在隐藏、卸载、销毁时放弃不需要的强引用，并清空 DrawingVisual 中的图片指令。重新显示时按保留的请求 ID 加载；播放或准备中待切换的 ID 只在暂停或停止后成为有效 ID。加载结果包含请求 ID、皮肤和独立诊断；缺包、无效包或预算不足不会把配置或草稿中的 ID 持久改成 `default`，保存事务状态与资源诊断分别显示。可以关闭其他预览，稍候重新选择皮肤或隐藏后重新打开来重试；补装缺失包后重显也会按原 ID 重试。过期和销毁后的异步结果不会重新激活控件。
