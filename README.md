# Studio

# NOTICE: Project has been temporarily suspended till further notice.

Check out the [original AssetStudio project](https://github.com/Perfare/AssetStudio) for more information.

Note: Requires Internet connection to fetch asset_index jsons.
_____________________________________________________________________________________________________________________________
How to use:

Check the tutorial [here](https://gist.github.com/Modder4869/0f5371f8879607eb95b8e63badca227e) (Thanks to Modder4869 for the tutorial)
_____________________________________________________________________________________________________________________________
CLI Version:
```
Description:

Usage:
  AssetStudioCLI <input_path> <output_path> [options]

Arguments:
  <input_path>   Input file/folder.
  <output_path>  Output folder.

Options:
  --silent                                                Hide log messages.
  --type <Texture2D|Sprite|etc..>                         Specify unity class type(s)
  --filter <filter>                                       Specify regex filter(s).
  --game <BH3|CB1|CB2|CB3|GI|SR|TOT|ZZZ> (REQUIRED)       Specify Game.
  --map_op <AssetMap|Both|CABMap|None>                    Specify which map to build. [default: None]
  --map_type <JSON|XML>                                   AssetMap output type. [default: XML]
  --map_name <map_name>                                   Specify AssetMap file name.
  --group_assets_type <ByContainer|BySource|ByType|None>  Specify how exported assets should be grouped. [default: 0]
  --no_asset_bundle                                       Exclude AssetBundle from AssetMap/Export.
  --no_index_object                                       Exclude IndexObject/MiHoYoBinData from AssetMap/Export.
  --xor_key <xor_key>                                     XOR key to decrypt MiHoYoBinData.
  --ai_file <ai_file>                                     Specify asset_index json file path (to recover GI containers).
  --version                                               Show version information
  -?, -h, --help                                          Show help and usage information
```
_____________________________________________________________________________________________________________________________
NOTES:
```
- in case of any "MeshRenderer/SkinnedMeshRenderer" errors, make sure to enable "Disable Renderer" option in "Export Options" before loading assets.
- in case of need to export models/animators without fetching all animations, make sure to enable "Ignore Controller Anim" option in "Options -> Export Options" before loading assets.
```
_____________________________________________________________________________________________________________________________
Special Thank to:
- Perfare: Original author.
- Khang06: [Project](https://github.com/khang06/genshinblkstuff) for extraction.
- Radioegor146: [Asset-indexes](https://github.com/radioegor146/gi-asset-indexes) for recovered/updated asset_index's.
- Ds5678: [AssetRipper](https://github.com/AssetRipper/AssetRipper)[[discord](https://discord.gg/XqXa53W2Yh)] for information about Asset Formats & Parsing.
- mafaca: [uTinyRipper](https://github.com/mafaca/UtinyRipper) for `YAML` and `AnimationClipConverter`. 

AssetStudio 1.36 (Razmoth) + Spine atlas 尺寸自动修复
=====================================================

这是什么
    解包（Export）之后自动检查每个 *.atlas 的 `size:` 行是否等于贴图真实像素尺寸，不一致就改到一致。
    成因：Unity 导入贴图时会按 2 的幂重采样页面（可能是非等比拉伸），而 atlas 里写的还是原始尺寸。
    Unity 的 Spine 运行时按「声明尺寸」归一化 UV，所以看着正常；
    libGDX 系运行时（含本项目的动态壁纸）按「贴图实际尺寸」归一化，必然错位。

用法（GUI）
    1. 直接运行 AssetStudio.GUI.exe，和原版完全一样。
    2. Options 菜单新增两项（默认已开第一项）：
         [x] Fix Spine atlas size after export
             批量导出结束后自动跑一遍修复。
         [ ] Atlas fix: resample texture (do not scale atlas)
             不勾（推荐）：等比时改 atlas 里的数字，贴图一个字节都不动。
             勾选：把贴图重采样到 atlas 声明的尺寸（非等比、或想保持 atlas 原样时用）。
    3. Misc. 菜单新增：Fix Spine atlas size in folder...
         选一个已经导出的目录；弹窗 是=修复，否=只检查不写盘，取消=退出。

用法（命令行，方便批处理）
    AssetStudio.CLI.exe --fix-atlas <导出目录> [--check] [--resample] [--scale-atlas]
        --check         只报告不写盘
        --resample      强制重采样贴图
        --scale-atlas   强制改 atlas 数字
        退出码 0=全部成功，1=有文件失败

判定规则（顺序）
    1. `size:` 已经等于贴图实际尺寸          -> 不动
    2. 两轴同比缩放（等比）                  -> 缩放 atlas 里所有数值字段，size 改成实际尺寸
    3. 非等比（Unity 非等比拉伸过）          -> 重采样贴图到声明尺寸
       等比才敢直接缩放数字，是因为 rotate:90/270 的 bounds 是按「未旋转」方向写的，
       等比缩放与该宽高交换可交换；非等比不行。
    4. 改写前先做「足迹核验」：所有 region 的 packed 矩形（90/270 交换宽高）叠到页面坐标系，
       最大范围必须落在声明的 size: 之内，否则说明判定前提不成立，直接放弃这个文件不写盘。

三条硬规矩（改代码前必读）
    * 绝不改写 `rotate:` 的值。
    * 判据用「足迹能否装下」，不要用「矩形外残留 alpha」（矩形超出画面时掩码被裁剪，会假性归零）。
    * 不用「同款素材像素相似度互证」当证据（有系统偏差，对照组也会给出同一个"答案"）。

改动的源码（在 ..\RazTools AssetStudio 源码\）
    AssetStudio.Utility/AtlasSizeFixer.cs        新增：解析 + 判定 + 改写 / 重采样
    AssetStudio.GUI/Studio.cs                    ExportAssets 收尾时调用修复器
    AssetStudio.GUI/MainForm.cs + .Designer.cs   两个 Options 勾选项 + Misc 菜单项
    AssetStudio.GUI/Properties/Settings.settings (+Designer.cs)  两个持久化设置
    AssetStudio.CLI/Program.cs                   --fix-atlas 独立入口（在加载资源之前就返回）

运行环境
    .NET 7 桌面运行时（原版 1.36 同样是 net7.0-windows，要求一致）。
