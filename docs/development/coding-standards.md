# 代码与工程规范

为了保持代码库的整洁、严谨与长久可维护性，所有提交至 BetterPetitPlanet 的代码均需遵守本规范。

---

## 1. 命名与命名空间约束

1. **统一根命名空间**：
   - 所有子项目、类库与测试代码的命名空间必须严格以 `BetterPetitPlanet` 为根基，例如：
     - `BetterPetitPlanet.GameCapture`
     - `BetterPetitPlanet.WindowsInput`
     - `BetterPetitPlanet.HotkeyCapture`
     - `BetterPetitPlanet.Core`

---

## 2. 文本与排版规范

1. **专业严谨的工程表述**：
   - 避免使用夸张、轻浮或空洞的表述，文档与代码注释聚焦技术机理、物理约束、参数范围与异常处理。

---

## 3. 非托管资源与内存管理规范

项目涉及大量的显存纹理、GDI 位图（HBITMAP/HDC）以及 OpenCV 矩阵（Mat）。必须严格遵循以下内存安全准则：

1. **IDisposable 严格配对**：
   - 任何实现了 `IDisposable` 的类（如 `GameCaptureFrame`、`Bitmap`、`Mat`），在使用完毕后必须通过 `using` 语句或在父级 `Dispose()` 中显式释放。
2. **避免大对象分配（LOH）**：
   - 高频帧捕获（如 60fps 流式处理）中，禁止频繁在堆上分配新的大字节数组。必须复用固定缓冲池或共享显存纹理，防止频繁触发垃圾回收（GC）。

---

## 4. Git 提交规范

1. **Conventional Commits 规范**：
   - 提交信息统一采用规范格式：`<type>(<scope>): <subject>`，例如：
     - `feat(ui): stream live official cover visuals`
     - `fix(capture): resolve DirectML context reentrancy`
     - `refactor(music): decouple instrument asset paths`
