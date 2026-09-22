using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace AssetStudio
{
    /// <summary>
    /// Which side is moved so that the atlas `size:` line and the real texture pixels agree.
    /// </summary>
    public enum AtlasFixDirection
    {
        /// <summary>
        /// Uniform ratio  -> scale the atlas (cheap, texture untouched).
        /// Non-uniform     -> resample the texture (Unity stretched the page anisotropically).
        /// </summary>
        Auto = 0,
        /// <summary>Always scale the atlas so `size:` equals the texture's real pixel size.</summary>
        ScaleAtlas = 1,
        /// <summary>Always resample the texture to the size declared in the atlas.</summary>
        ResampleTexture = 2,
    }

    public enum AtlasFixAction
    {
        Unchanged,
        AtlasScaled,
        TextureResampled,
        Skipped,
        Failed,
    }

    public sealed class AtlasFixOptions
    {
        public AtlasFixDirection Direction = AtlasFixDirection.Auto;
        public bool DryRun;
    }

    public sealed class AtlasFixResult
    {
        public string AtlasPath;
        public AtlasFixAction Action;
        public string Message;
        public int DeclaredWidth;
        public int DeclaredHeight;
        public int ActualWidth;
        public int ActualHeight;
        public long AtlasBytesBefore;
        public long AtlasBytesAfter;

        public override string ToString()
        {
            return $"{Path.GetFileName(AtlasPath)} => {Action}: {Message}";
        }
    }

    /// <summary>
    /// Makes the atlas `size:` line agree with the real texture pixel size.
    ///
    /// Why this matters: Unity resizes a texture page to power-of-two on import (possibly
    /// anisotropically). The Spine C#/Unity runtime normalises UVs with the size DECLARED in the
    /// atlas, so it looks right inside Unity; libGDX-style runtimes normalise with the ACTUAL
    /// loaded texture size, so the very same files render misplaced.
    ///
    /// Two ways to reach agreement:
    ///   1. scale the atlas numbers  - only safe when the ratio is the same on both axes,
    ///      because a `bounds:` rect of a rotate:90/270 region is stored in the un-rotated
    ///      orientation while its footprint on the page is swapped. Uniform scaling commutes with
    ///      that swap, anisotropic scaling does not.
    ///   2. resample the texture     - always usable, costs one full image resize.
    /// </summary>
    public static class AtlasSizeFixer
    {
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp", ".ktx", ".etc2", ".etc1", ".astc" };

        // ---------------------------------------------------------------- public API

        /// <summary>
        /// Fixes every Spine atlas found under <paramref name="root"/> (recursively).
        /// </summary>
        public static List<AtlasFixResult> FixDirectory(string root, AtlasFixOptions options = null, Action<string> log = null)
        {
            var results = new List<AtlasFixResult>();
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                log?.Invoke($"atlas fix: directory not found: {root}");
                return results;
            }

            options = options ?? new AtlasFixOptions();

            List<string> atlases;
            try
            {
                atlases = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .Where(p => !Path.GetFileName(p).StartsWith("_"))       // skip our own intermediates
                    .Where(p => Path.GetFileName(p).IndexOf(".atlas", StringComparison.OrdinalIgnoreCase) >= 0 || IsTextLikeExtension(p))
                    .Where(p => new FileInfo(p).Length <= 8 * 1024 * 1024)
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                log?.Invoke($"atlas fix: enumerate '{root}' failed: {ex.Message}");
                return results;
            }

            foreach (var atlas in atlases)
            {
                string text;
                try
                {
                    text = ReadText(atlas, out _);
                }
                catch (Exception ex)
                {
                    var bad = new AtlasFixResult { AtlasPath = atlas, Action = AtlasFixAction.Failed, Message = "read error: " + ex.Message };
                    results.Add(bad);
                    log?.Invoke("atlas fix: " + bad);
                    continue;
                }

                // content sniffing: a Spine atlas always carries a page `size:` line and region rects
                if (IndexOfLine(text, "size:") < 0 || IndexOfLine(text, "bounds:") < 0)
                    continue;

                var result = FixFile(atlas, root, options, log);
                results.Add(result);
            }

            return results;
        }

        public static AtlasFixResult FixFile(string atlasPath, string searchRoot, AtlasFixOptions options = null, Action<string> log = null)
        {
            options = options ?? new AtlasFixOptions();
            var result = new AtlasFixResult { AtlasPath = atlasPath };

            try
            {
                var text = ReadText(atlasPath, out var hasBom);
                result.AtlasBytesBefore = new FileInfo(atlasPath).Length;

                var newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
                if (newline == "\n" && text.IndexOf("\r", StringComparison.Ordinal) >= 0) newline = "\r\n";
                var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

                var blocks = SplitBlocks(lines);
                foreach (var b in blocks) Classify(b, lines);

                var pages = blocks.Where(b => b.IsPage).ToList();
                if (pages.Count == 0)
                {
                    result.Action = AtlasFixAction.Skipped;
                    result.Message = "no page header found";
                    log?.Invoke("atlas fix: " + result);
                    return result;
                }

                var dir = Path.GetDirectoryName(atlasPath);

                // resolve the texture of every page
                var pageOfBlock = new Dictionary<Block, Block>();
                var current = (Block)null;
                foreach (var b in blocks)
                {
                    if (b.IsPage) { current = b; continue; }
                    if (b.IsRegion && current != null) pageOfBlock[b] = current;
                }

                var textures = new Dictionary<Block, string>();
                foreach (var page in pages)
                {
                    if (page.DeclaredW <= 0 || page.DeclaredH <= 0)
                    {
                        result.Action = AtlasFixAction.Skipped;
                        result.Message = $"page '{page.Name}' has no usable 'size:' line";
                        log?.Invoke("atlas fix: " + result);
                        return result;
                    }

                    var path = ResolveTexture(dir, page.Name);
                    if (path == null)
                    {
                        result.Action = AtlasFixAction.Skipped;
                        result.Message = $"texture '{page.Name}' not found next to the atlas nor under '{searchRoot}'";
                        log?.Invoke("atlas fix: " + result);
                        return result;
                    }
                    textures[page] = path;

                    var info = Identify(path);
                    if (info == null)
                    {
                        result.Action = AtlasFixAction.Skipped;
                        result.Message = $"cannot read image size of '{Path.GetFileName(path)}'";
                        log?.Invoke("atlas fix: " + result);
                        return result;
                    }
                    page.ActualW = info.Value.Width;
                    page.ActualH = info.Value.Height;
                }

                result.DeclaredWidth = pages[0].DeclaredW;
                result.DeclaredHeight = pages[0].DeclaredH;
                result.ActualWidth = pages[0].ActualW;
                result.ActualHeight = pages[0].ActualH;

                var mismatched = pages.Where(p => p.DeclaredW != p.ActualW || p.DeclaredH != p.ActualH).ToList();
                if (mismatched.Count == 0)
                {
                    result.Action = AtlasFixAction.Unchanged;
                    result.Message = $"size {result.DeclaredWidth}x{result.DeclaredHeight} already matches {Path.GetFileName(textures[pages[0]])}";
                    log?.Invoke("atlas fix: " + result);
                    return result;
                }

                // decide the direction (per page, because pages may scale differently)
                var modes = new Dictionary<Block, AtlasFixDirection>();
                foreach (var page in mismatched)
                {
                    var uniform = IsUniform(page);
                    var dirChoice = options.Direction;
                    if (dirChoice == AtlasFixDirection.Auto)
                        dirChoice = uniform ? AtlasFixDirection.ScaleAtlas : AtlasFixDirection.ResampleTexture;
                    if (dirChoice == AtlasFixDirection.ScaleAtlas && !uniform)
                    {
                        log?.Invoke($"atlas fix: {Path.GetFileName(atlasPath)} page '{page.Name}' is scaled anisotropically " +
                                    $"({page.ActualW / (double)page.DeclaredW:0.####} x {page.ActualH / (double)page.DeclaredH:0.####}) " +
                                    "-> falling back to resampling the texture (bounds cannot be re-scaled per axis for rotated regions)");
                        dirChoice = AtlasFixDirection.ResampleTexture;
                    }
                    modes[page] = dirChoice;
                }

                // precondition check for ScaleAtlas: the region rects must live inside the declared page
                foreach (var page in mismatched.Where(p => modes[p] == AtlasFixDirection.ScaleAtlas))
                {
                    var regions = blocks.Where(b => b.IsRegion && pageOfBlock.TryGetValue(b, out var pg) && pg == page).ToList();
                    var fit = FootprintFits(regions, page.DeclaredW, page.DeclaredH);
                    if (!fit)
                    {
                        result.Action = AtlasFixAction.Failed;
                        result.Message = $"region footprint exceeds the declared page {page.DeclaredW}x{page.DeclaredH}; " +
                                         "the atlas does not describe this texture, refusing to touch it";
                        log?.Invoke("atlas fix: " + result);
                        return result;
                    }
                }

                // ---- build the new atlas text
                var output = new List<string>(lines);
                var scaledPages = new List<string>();
                var resampled = new List<string>();

                foreach (var page in mismatched)
                {
                    if (modes[page] == AtlasFixDirection.ScaleAtlas)
                    {
                        var sx = page.ActualW / (double)page.DeclaredW;
                        var sy = page.ActualH / (double)page.DeclaredH;

                        for (int i = page.Start; i < page.End; i++)
                        {
                            var key = KeyOf(output[i]);
                            if (key == "size")
                                output[i] = SetKey(output[i], $"{page.ActualW},{page.ActualH}");
                        }

                        foreach (var kv in pageOfBlock.Where(kv => kv.Value == page))
                        {
                            var b = kv.Key;
                            // remember the rotate:90/270 state for the footprint log only - never rewritten
                            for (int i = b.Start; i < b.End; i++)
                            {
                                var key = KeyOf(output[i]);
                                if (key == null) continue;
                                var value = ValueOf(output[i]);
                                var newValue = ScaleField(key, value, sx, sy);
                                if (newValue != null) output[i] = SetKey(output[i], newValue);
                            }
                        }

                        scaledPages.Add($"{page.Name}: size {page.DeclaredW}x{page.DeclaredH} -> {page.ActualW}x{page.ActualH} (x{sx:0.####})");
                    }
                    else
                    {
                        resampled.Add($"{page.Name}: {page.ActualW}x{page.ActualH} -> {page.DeclaredW}x{page.DeclaredH}");
                    }
                }

                if (resampled.Count > 0 && !options.DryRun)
                {
                    foreach (var page in mismatched.Where(p => modes[p] == AtlasFixDirection.ResampleTexture))
                    {
                        var path = textures[page];
                        Resample(path, page.DeclaredW, page.DeclaredH);
                        log?.Invoke($"atlas fix: resampled {Path.GetFileName(path)} {page.ActualW}x{page.ActualH} -> {page.DeclaredW}x{page.DeclaredH}");
                    }
                }

                if (scaledPages.Count > 0 && !options.DryRun)
                {
                    File.WriteAllText(atlasPath, string.Join(newline, output), new UTF8Encoding(hasBom));
                    result.AtlasBytesAfter = new FileInfo(atlasPath).Length;
                }

                var parts = new List<string>();
                if (scaledPages.Count > 0) parts.Add("atlas scaled [" + string.Join("; ", scaledPages) + "]");
                if (resampled.Count > 0) parts.Add("texture resampled [" + string.Join("; ", resampled) + "]");
                result.Action = scaledPages.Count > 0 ? AtlasFixAction.AtlasScaled : AtlasFixAction.TextureResampled;
                if (options.DryRun) parts.Add("(dry run, nothing written)");
                result.Message = string.Join(", ", parts);
                log?.Invoke("atlas fix: " + result);
                return result;
            }
            catch (Exception ex)
            {
                result.Action = AtlasFixAction.Failed;
                result.Message = ex.Message;
                log?.Invoke($"atlas fix: {Path.GetFileName(atlasPath)} failed: {ex}");
                return result;
            }
        }

        // ---------------------------------------------------------------- blocks

        private sealed class Block
        {
            public int Start;
            public int End;                 // exclusive
            public string Name = "";
            public bool IsPage;
            public bool IsRegion;
            public int DeclaredW;
            public int DeclaredH;
            public int ActualW;
            public int ActualH;
            /// <summary>packed rect as written (x, y, w, h), null when the block is not a region</summary>
            public double[] Rect;
            /// <summary>rotate: 90 or 270</summary>
            public bool Rotated;
        }

        /// <summary>
        /// A block starts at a line without a ':' (the page image name or a region name) and lasts
        /// until the next such line. Blank lines are optional separators - Spine writes them, some
        /// Unity-embedded copies do not, so they must not be relied on.
        /// </summary>
        private static List<Block> SplitBlocks(string[] lines)
        {
            var blocks = new List<Block>();
            Block current = null;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (KeyOf(line) == null)
                {
                    current = new Block { Start = i, End = i + 1 };
                    blocks.Add(current);
                }
                else if (current != null)
                {
                    current.End = i + 1;
                }
            }

            return blocks;
        }

        private static void Classify(Block b, string[] lines)
        {
            var hasPageSize = false;
            var hasPageKeys = false;
            var hasRect = false;
            var hasLegacyRect = false;
            var declaredW = 0;
            var declaredH = 0;
            double[] bounds = null;
            double[] xy = null;
            double[] regionSize = null;
            var rotated = false;

            for (var i = b.Start; i < b.End; i++)
            {
                var key = KeyOf(lines[i]);
                if (key == null) continue;
                var value = ValueOf(lines[i]);
                double[] parsed;
                switch (key)
                {
                    case "size":
                        if (TryParseDoubles(value, out parsed) && parsed.Length >= 2)
                        {
                            hasPageSize = true;
                            declaredW = (int)Math.Round(parsed[0]);
                            declaredH = (int)Math.Round(parsed[1]);
                            regionSize = parsed;
                        }
                        break;
                    case "format":
                    case "filter":
                    case "repeat":
                    case "pma":
                    case "scale":
                        hasPageKeys = true;
                        break;
                    case "bounds":
                        if (TryParseDoubles(value, out parsed) && parsed.Length == 4) bounds = parsed;
                        hasRect = true;
                        break;
                    case "xy":
                        if (TryParseDoubles(value, out parsed) && parsed.Length == 2) xy = parsed;
                        hasLegacyRect = true;
                        break;
                    case "orig":
                    case "offset":
                    case "offsets":
                        hasLegacyRect = true;
                        break;
                    case "rotate":
                        rotated = value == "90" || value == "270";
                        break;
                }
            }

            b.Name = lines[b.Start].Trim();
            b.DeclaredW = declaredW;
            b.DeclaredH = declaredH;
            b.Rotated = rotated;
            b.IsPage = hasPageSize && !hasRect && !hasLegacyRect && (hasPageKeys || LooksLikeImageName(b.Name) || b.Start == 0);
            b.IsRegion = !b.IsPage && (hasRect || hasLegacyRect);
            if (bounds != null)
                b.Rect = bounds;
            else if (xy != null && regionSize != null && regionSize.Length >= 2)
                b.Rect = new[] { xy[0], xy[1], regionSize[0], regionSize[1] };
        }

        private static bool LooksLikeImageName(string name)
        {
            var ext = Path.GetExtension(name);
            if (string.IsNullOrEmpty(ext)) return false;
            return ImageExtensions.Contains(ext.ToLowerInvariant());
        }

        /// <summary>
        /// AssetStudio writes a TextAsset with whatever extension it was able to restore
        /// (.atlas, .txt, .prefab, .bytes, ...), so the extension alone is not a reliable filter -
        /// the content sniff in FixDirectory decides.
        /// </summary>
        private static bool IsTextLikeExtension(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".atlas":
                case ".txt":
                case ".prefab":
                case ".bytes":
                case ".json":
                case ".asset":
                case "":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsUniform(Block page)
        {
            var sx = page.ActualW / (double)page.DeclaredW;
            var sy = page.ActualH / (double)page.DeclaredH;
            return Math.Abs(sx - sy) <= 1e-4 * Math.Max(sx, sy);
        }

        /// <summary>
        /// max(x + w, y + h) over all regions, with w/h swapped for rotate:90/270 regions
        /// (their `bounds:` rect is written in the un-rotated orientation).
        /// </summary>
        private static bool FootprintFits(List<Block> regions, int pageW, int pageH)
        {
            double maxX = 0, maxY = 0;
            foreach (var r in regions)
            {
                var rect = FindRect(r);
                if (rect == null) continue;
                var rotated = r.Rotated;
                var w = rotated ? rect[3] : rect[2];
                var h = rotated ? rect[2] : rect[3];
                maxX = Math.Max(maxX, rect[0] + w);
                maxY = Math.Max(maxY, rect[1] + h);
            }
            return maxX <= pageW && maxY <= pageH;
        }

        private static double[] FindRect(Block b) => b.Rect;

        // ---------------------------------------------------------------- field maths

        /// <summary>
        /// Scales one region field. `bounds`/`offsets`/`split`/`pad` are x,y,w,h; `xy`/`offset` are x,y;
        /// `size`/`orig` are w,h. Returns null when the field must be left alone.
        /// </summary>
        private static string ScaleField(string key, string value, double sx, double sy)
        {
            if (!TryParseDoubles(value, out var n)) return null;

            switch (key)
            {
                case "bounds":
                case "offsets":
                case "split":
                case "pad":
                    if (n.Length != 4) return null;
                    n[0] *= sx; n[1] *= sy; n[2] *= sx; n[3] *= sy;
                    break;
                case "xy":
                case "offset":
                    if (n.Length != 2) return null;
                    n[0] *= sx; n[1] *= sy;
                    break;
                case "size":
                case "orig":
                    if (n.Length != 2) return null;
                    n[0] *= sx; n[1] *= sy;
                    break;
                default:
                    return null;
            }

            var isSize = key == "size" || key == "orig";
            for (var i = 0; i < n.Length; i++)
            {
                var v = (int)Math.Round(n[i], MidpointRounding.AwayFromZero);
                if (isSize && v < 1) v = 1;
                n[i] = v;
            }

            var sb = new StringBuilder();
            for (var i = 0; i < n.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(((int)n[i]).ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // ---------------------------------------------------------------- io helpers

        private static string ReadText(string path, out bool hasBom)
        {
            var bytes = File.ReadAllBytes(path);
            hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var offset = hasBom ? 3 : 0;
            var text = new UTF8Encoding(false).GetString(bytes, offset, bytes.Length - offset);
            return text.TrimStart('\uFEFF');
        }

        private static string KeyOf(string line)
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) return null;
            var key = line.Substring(0, colon).Trim();
            if (key.IndexOfAny(new[] { ' ', '\t' }) >= 0) return null;
            return key;
        }

        private static string ValueOf(string line)
        {
            var colon = line.IndexOf(':');
            return colon <= 0 ? null : line.Substring(colon + 1).Trim();
        }

        private static string SetKey(string line, string value)
        {
            var colon = line.IndexOf(':');
            return line.Substring(0, colon + 1) + value;
        }

        private static int IndexOfLine(string text, string key)
        {
            using (var reader = new StringReader(text))
            {
                string line;
                for (var i = 0; (line = reader.ReadLine()) != null; i++)
                {
                    if (line.TrimStart().StartsWith(key, StringComparison.Ordinal)) return i;
                }
            }
            return -1;
        }

        private static bool TryParseDoubles(string value, out double[] result)
        {
            result = null;
            if (string.IsNullOrEmpty(value)) return false;
            var parts = value.Split(',');
            var values = new double[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    return false;
            }
            result = values;
            return true;
        }

        private static (int Width, int Height)? Identify(string path)
        {
            try
            {
                var info = Image.Identify(path);
                if (info == null) return null;
                return (info.Width, info.Height);
            }
            catch
            {
                return null;
            }
        }

        private static void Resample(string path, int width, int height)
        {
            var info = Identify(path);
            if (info == null || (info.Value.Width == width && info.Value.Height == height)) return;

            var upscale = width > info.Value.Width || height > info.Value.Height;
            // ImageSharp resizes in premultiplied alpha space, which keeps translucent edges clean.
            var resampler = upscale ? KnownResamplers.Bicubic : KnownResamplers.Box;

            // keep the real extension last so the encoder can be picked from it
            var tmp = path + ".tmp" + Path.GetExtension(path);
            using (var image = Image.Load(path))
            {
                image.Mutate(x => x.Resize(width, height, resampler));
                image.Save(tmp);
            }

            File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>
        /// Finds the texture of a page. Probes the layouts AssetStudio produces, then falls back to a
        /// lookup by file name inside the atlas folder (never outside of it - two models in the same
        /// export tree can easily share a page name).
        /// </summary>
        private static string ResolveTexture(string atlasDir, string pageName)
        {
            if (string.IsNullOrEmpty(pageName)) return null;

            var fileName = Path.GetFileName(pageName);
            var probes = new List<string>();
            if (!string.IsNullOrEmpty(atlasDir))
            {
                probes.Add(Path.Combine(atlasDir, pageName));          // page name may contain sub folders
                probes.Add(Path.Combine(atlasDir, fileName));
                probes.Add(Path.Combine(atlasDir, "Texture2D", fileName));
                var parent = Path.GetDirectoryName(atlasDir);
                if (!string.IsNullOrEmpty(parent))
                {
                    probes.Add(Path.Combine(parent, "Texture2D", fileName));
                    probes.Add(Path.Combine(parent, fileName));
                }
            }

            foreach (var probe in probes)
            {
                if (File.Exists(probe)) return probe;
            }

            if (!string.IsNullOrEmpty(atlasDir) && Directory.Exists(atlasDir))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(atlasDir, fileName, SearchOption.AllDirectories))
                    {
                        return file;
                    }
                }
                catch
                {
                    // ignored - reported as "not found"
                }
            }

            return null;
        }
    }
}
