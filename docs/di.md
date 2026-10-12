# 依赖注入（DI）

- 容器：`Microsoft.Extensions.DependencyInjection`；Page/Window 由框架实例化，用字段式 `App.GetService<T>()` 取（Service Locator 过渡方案，可接受）。
- 生命周期：全局单例用 `AddSingleton`；需要每实例隔离的才 `AddTransient`。注意 `Transient` 只保证“每次解析给新实例”，回收仍完全交给 GC——频繁开关的重型 UI 不要靠 `Transient` 反复新建，见 `heavy-ui.md`。
- 组件归属：
  - `MemeDataEngine` 进容器（核心数据层）。
  - `Localization` / `Logger` / `LangHelper` / `EcoQos` / `Utils` 保持 static（无状态工具，强行进容器会让 100+ 处调用改签名，得不偿失）。
  - `ImageDragHelper` 保持 static（View 层 UI 适配器，非 Service）。
  - `FileWatcher` 作为 Engine 成员随其注入，不单独注册。
