
## Curb 能用但是目前有毛病

[Curb](https://github.com/nullean/curb) 格式化csharp代码确实飞快, 本来 [Directory.Build.props](../Directory.Build.props) 也准备好了

但是目前(周一 10月 12 18:53:22 CST 2026) nutget 源上还是只有0.7.3版本, 格式化和check的时候会遇到下面的问题

```log
PS MemeManager> curb format
MemeManager\App.xaml.cs: formatting changed token 1272: 'try' became '}'
MemeManager\Services\ClipboardService.cs: formatting changed token 327: 'try' became '}'
Formatted 76 file(s) in 22ms — 0 changed, 2 failed, 0 unparsable, printer coverage 100.0 %
  0.61 MB source, allocated 23.8 MB (39.0x source)
  round-trip verified 2 of 76 file(s) (2.6 % needed a second parse)
PS MemeManager> 
```

这导致没法直接嵌入到dotnet build命令中了(依赖这个包之后dotnet build因为Curb失败而失败了)

但是format功能还是很正常的, 速度比`dotnet format`快了不知道多少倍, 只是format后会有一些换行和`dotnet format`
似乎不知道能不能`.editconfig`限制多少字符强制换行

所以先搁置

## dotnet format

太慢了, format完都要20s了, 真不想用吧
