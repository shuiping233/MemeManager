# 不做的过度工程（既定决策）

## MemeDataEngine 不再往下硬拆

`MemeDataEngine` 作为数据访问层职责已自洽，不再往下硬拆 Repository / 写操作迁入 Service / 引擎降级 Facade（只会增加缓存一致性风险、运行行为零变化、用户无感）。唯一抽离的是 `ConfigService`（配置与图片数据是两回事）。

## 拖拽逻辑不重构

拖拽是“View 决策 + ViewModel 执行”混合体，不搬进 VM、不新增 `DragDropHelper` 附加属性、不拆独立 Service。`ImageDragHelper`（static 类）作为 View 层适配器，把 `DataView` / `DataPackageView` 萃取成纯 `List<string>`（图片过滤 + Bitmap 落临时文件），code-behind 的 `Drop`/`DragItemsCompleted` 拿纯数据后交给 Service/VM。强行抽成 Service 或附加属性只重复萃取逻辑、不提升解耦度。

## Community Toolkit

- 允许使用 community toolkit 的 UI 控件和类库，方便、能减少重复代码。
- 推荐用 `WeakReferenceMessenger` 单向消息做跨组件（VM↔Page、跨窗口/控件）能力调用与状态广播，判据与纪律见 `vm-to-page.md`。
