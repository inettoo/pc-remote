# 编码规范
C# nullable、隐式 using、编译警告视为错误；4 空格，UTF-8，dotnet format。
公共 API PascalCase，局部 camelCase；异步方法 Async 后缀；CancellationToken 放末尾。
优先 record 表达消息、接口表达跨模块能力；不要为了未来假设引入框架或抽象。
UI code-behind 只负责绑定/视图事件；业务进入 Controller/Host。
预期失败返回可呈现状态；意外异常在应用边界记录与显示，禁止空 catch。日志脱敏、不存输入内容。
新依赖需解释用途、维护情况、许可和替代方案，并写 ADR。当前无第三方运行依赖。
