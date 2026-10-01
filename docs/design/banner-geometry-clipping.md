# 封面 Banner 几何裁剪工程实践

在 WPF 桌面开发中，实现类似于 BetterGI 或现代 Fluent UI 卡片的圆角图像容器看似简单，但在包含多图层叠加、动态缩放以及流式远程图片时，存在数个极其隐蔽的渲染机制陷阱。本文档详细记录相关机理及最终的技术解决方案。

---

## 1. 传统 WPF 裁剪机理的局限

### 1.1 Border.ClipToBounds 的几何局限
在 WPF 标准实现中，为外层容器设置 `<Border CornerRadius="8" ClipToBounds="True">` 并不能约束内部的 `<Image>` 子元素。
- **原因**：WPF 内部的 `ClipToBounds` 是基于外接矩形（`Rect`）进行轴对齐剔除的。它并不考虑 `Border` 的 `CornerRadius` 曲线。
- **现象**：当子级 `<Image>` 使用 `Stretch="UniformToFill"` 撑满容器时，图片的 4 个 90 度直角会直接覆盖在外层 Border 的圆角之上，形成明显的直角锯齿外露。

---

## 2. 方案尝试与失效机理剖析

### 2.1 尝试方案：使用 VisualBrush 作为 OpacityMask
为了约束圆角，最初的尝试是在内部容器上施加 `OpacityMask`，并使用 `VisualBrush` 引用一个具有 `CornerRadius="8"` 的遮罩 Border：

```xml
<Grid>
    <Grid.OpacityMask>
        <VisualBrush Visual="{Binding ElementName=BannerMask}" />
    </Grid.OpacityMask>
    ...
</Grid>
```

该方案在静态单一矩形下看似可行，但在多图层复杂布局下引发了两个严重的视觉缺陷：

#### 缺陷 1：负边距导致元素伸出卡片底边缘（尖刺刺出）
- **现象**：右侧人物插画为了营造画面的立体溢出感，最初设置了 `Height="230"` 与 `Margin="0,0,18,-18"`。这导致望远镜的三脚架支架向卡片底边缘之外下探了 18 像素，在卡片底部形成了一根明显的尖刺。

#### 缺陷 2：VisualBrush 纵向拉伸导致卡片底部圆角变成平角
- **核心机理**：
  1. `VisualBrush` 默认采用 `ViewportUnits="RelativeToBoundingBox"`。
  2. 当子级图像设置了负边距 `Margin="0,0,18,-18"` 时，该容器在 WPF 渲染树中的**墨迹包围盒（Ink Bounding Box）**高度被整体撑大到了 218 像素（0 到 218）。
  3. `VisualBrush` 会将高度为 200 的 `BannerMask` 等比拉伸映射到 0 到 218 的高度区间上。
  4. 最终导致：遮罩顶部的圆角映射在 y=0 处（顶部正常圆角），而遮罩底部的圆角被拉伸映射到了 y=218（尖刺最底部）！而在卡片真正的底部边缘（y=200 处），蒙版处于中间直筒区域，因而在视觉上卡片底部的两个角完全变成了 90 度平直角。

#### 缺陷 3：ClearType 次像素抗锯齿失效
- 使用 `OpacityMask` 会强制 WPF 渲染管线将整个 Visual 树栅格化到离屏 Alpha 纹理缓冲区。该中间纹理在合成时无法使用操作系统的 ClearType 次像素渲染，导致位于蒙版内部的文字边缘发虚、对比度显著下降。

---

## 3. 最终工程解决方案：原生硬件几何裁切

为了彻底规避 `OpacityMask` 带来的尺寸拉伸与字体发虚问题，项目采用了**原生硬件级 `UIElement.Clip` + 尺寸事件驱动**的方案。

### 3.1 消除图像越界溢出
在 [HomePage.xaml](../../BetterPetitPlanet/View/Pages/HomePage.xaml) 中，约束右侧人物插画高度与内边距：
```xml
<!-- 底部与卡片平齐，消除突出尖刺 -->
<Image Source="{Binding OfficialForegroundUrl}"
       Height="196"
       HorizontalAlignment="Right"
       VerticalAlignment="Bottom"
       Margin="0,0,20,0"
       Stretch="Uniform"
       RenderOptions.BitmapScalingMode="HighQuality" />
```
确保所有图像元素的物理外接矩形严格收敛在卡片 200 像素的高度范围之内。

### 3.2 动态施加 RectangleGeometry 硬件裁切
在代码后置（[HomePage.xaml.cs](../../BetterPetitPlanet/View/Pages/HomePage.xaml.cs)）中响应卡片的 `SizeChanged` 事件：

```csharp
private void BannerCard_SizeChanged(object sender, SizeChangedEventArgs e)
{
    if (sender is UIElement element && e.NewSize.Width > 0 && e.NewSize.Height > 0)
    {
        element.Clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 8, 8);
    }
}
```

### 3.3 方案优势
1. **绝对严格对称**：裁剪范围严格限定在 `(0, 0, ActualWidth, ActualHeight)`，且无论窗体如何缩放，左上、右上、左下、右下 4 个角均强制按照半径 8 像素的连续圆弧执行几何裁剪，彻底消除了底部平角与尖刺。
2. **零离屏渲染损耗**：`UIElement.Clip` 直接转换为 DirectX 底层的几何裁剪面或模板测试（Stencil Test），不生成额外的中间纹理。
3. **字体渲染保真**：文本位于裁剪区域内部，完全保留原生的 ClearType 次像素抗锯齿，文字清晰锐利。
