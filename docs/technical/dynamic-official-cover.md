# 官网主视觉动态流式解析

为了保持客户端轻量，同时保证主页视觉能够随着《星布谷地》官方版本的演进（如公测、大版本主题更新）实时呈现最新插画，BetterPetitPlanet 实现了**基于官网 Nuxt 打包清单逆向分析的动态流式封面解析服务（OfficialCoverService）**。

---

## 1. 核心设计约束

1. **绝对零落盘（Zero Disk Write）**：
   - 绝不将封面图片文件下载并缓存至用户本地磁盘（不占用用户存储空间，避免产生垃圾临时文件）。
   - 解析出的图片资源统一直接以 HTTPS 远程 URI 形式注入 WPF 的 `BitmapImage`，在客户端内存中流式解压呈现。
2. **拒绝写死静态链接**：
   - 现代前端项目（Nuxt/Webpack）构建时，静态图片资源均带有内容哈希（如 `bg-1.6a2cfc2.jpg`）。官网一旦发布补丁或换版，哈希值变动将导致写死的静态链接彻底 404 失效。
   - 客户端必须能够自主从官网首页逆向提取最新的资源哈希。

---

## 2. Nuxt 动态分块逆向解析流程

星布谷地官网（`https://planet.mihoyo.com`）由 Nuxt.js 生成。主视觉图片的绝对路径被打包在按路由懒加载的代码分块（Chunks）中。解析管线如下：

```text
[GET https://planet.mihoyo.com/home]
              │
              ▼
   提取主脚本引用: /_nuxt/*.js
              │
              ▼
   下载并解析主脚本，匹配:
   1. Chunk ID 映射表: { 0: "hash0", 1: "hash1", ... }
   2. /home 路由依赖分块: path: "/home", ... Promise.all([.e(24), .e(35)])
              │
              ▼
   按需下载目标 Chunk 脚本 (如: /_nuxt/24.[hash].js)
              │
              ▼
   正则正则抽取高分辨率资源标识:
   - 远景小镇图层: img/(bg-[^"'\s]+\.(?:jpg|png|webp))
   - 星空背景图层: img/(starry-sky[^"'\s]+\.(?:jpg|png|webp))
   - 人物弯月前景: img/(character-foreground[^"'\s]+\.(?:jpg|png|webp))
              │
              ▼
   合成完整绝对链接并异步通知 UI 呈现
```

### 容灾降级机制
解析任务在程序启动时由后台任务异步执行，超时限制为 6 秒。在弱网或无法连接官网的离线状态下，系统自动回退至内置的安全默认官方链接，确保 UI 绝不留白。

---

## 3. 三层视差多图层复合架构

在 [HomePage.xaml](../../BetterPetitPlanet/View/Pages/HomePage.xaml) 中，封面并非单一扁平图片，而是由三层结构复合而成：
1. **底层（Background）**：星布谷地远景小镇插画，采用 `UniformToFill` 撑满整个视口，不透明度设为 0.85。
2. **中层（Starry Sky）**：官网实时星空背景图层，提供点点星光与夜空层次感。
3. **顶层（Foreground）**：右侧人物与弯月插画（双人合奏），采用 `Uniform` 保持比例靠右下锚定。
4. **渐变遮罩**：左侧施加从 `#F20B0F19` 到 `Transparent` 的水平线性渐变蒙版，确保文字高对比度与清爽阅读。

---

## 4. 未来动态动画与视频背景规划

当前主页封面采用静态多图层流式复合。在后续迭代中，系统计划进一步升级视觉表现：

- **动态动画支持规划**：
  - 官网首页若开放包含 CSS 关键帧位移或 Canvas 粒子特效的微动效资源，解析器将扩充对动态 SVG 或 APNG 的支持。
  - 调研引入轻量级 DirectComposition / MediaElement 播放层，实现类似官方启动器（Launcher）的循环动态视频（Live Wallpaper / MP4 Loop）背景，为用户提供沉浸式的视听体验。
