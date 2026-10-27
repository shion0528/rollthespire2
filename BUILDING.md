# 构建resource.17

需要.NET 9 SDK、项目指定的Godot.NET.Sdk 4.5.1，以及合法安装的游戏程序集。运行Mod时沿用游戏自带运行环境；SDK仅用于源码构建。

Windows上项目会尝试从Steam注册表定位游戏。也可以通过本机 `local.props` 设置 `Sts2Path` / `Sts2ReferenceDir`；不要提交该私人配置。

在仓库根目录执行：

```powershell
dotnet build RolltheSpire2.csproj -c Release -p:EnableGpuProfiling=false -p:Rt2ModsDir=
```

`Rt2ModsDir`留空用于构建而不自动部署；正式发布保持 `EnableGpuProfiling=false`。默认输出位于 `.godot/mono/temp/bin/Release/`，分发所需文件只有生成的DLL与根目录JSON manifest。不要分发游戏DLL、Godot缓存、调试文件或本地状态。

原正式二进制使用既有构建环境生成；不同SDK、依赖或构建路径的重建产物不保证字节一致。本次整理核对的是生产源码字节与稳定17冻结快照一致，并未重新构建或改变正式DLL。

tests中部分历史验证入口依赖原实验夹具，不能仅凭源码目录宣称全部测试可以一键复现。随附公开测速输入和逐轮计数用于核对记录及自行设计同条件复测。
