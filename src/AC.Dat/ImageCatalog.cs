using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace AC.Dat
{
    /// <summary>
    /// Turns an image key into pixels, from whichever of the three places the Decal look's
    /// artwork lives in, and remembers the answer.
    /// </summary>
    /// <remarks>
    /// Keys name their source by prefix:
    /// <list type="bullet">
    /// <item><c>portal:0600126F</c> - an image in the client's portal archive, by hex id, with or without <c>0x</c>.</item>
    /// <item><c>vvs:Decal_Theme_Images.TabActiveLeft.png</c> - a PNG embedded in VirindiViewService.dll, by the end of its resource name.</item>
    /// <item><c>decal:Switch-Active.bmp</c> - a bitmap (or PNG) file in Decal's install directory.</item>
    /// </list>
    /// The Decal and Virindi images mark their see-through parts with pure cyan rather than
    /// alpha, so those two are colour-keyed on the way in; the client's own images carry real
    /// alpha and are left as they are.
    ///
    /// <para>
    /// Every answer is cached, failures included: an image that is missing stays missing for
    /// the session, and asking again should not cost another trip to the disk. Lookups are
    /// safe from any thread, and each key is resolved once even when two threads ask for it
    /// at the same moment - the host resolves in the background while the game's thread reads.
    /// </para>
    /// </remarks>
    public sealed class ImageCatalog
    {
        public const string PortalPrefix = "portal:";
        public const string VirindiViewServicePrefix = "vvs:";
        public const string DecalPrefix = "decal:";

        /// <summary>
        /// An image the host carries itself, for art no install has as a file - Decal's icon,
        /// which lives in its agent's resources: <c>host:decal</c>.
        /// </summary>
        public const string HostPrefix = "host:";

        /// <summary>
        /// Decal's bar switches, made from its own bitmaps: <c>bar:open</c>, <c>bar:closed</c>
        /// and <c>bar:faulted</c>.
        /// </summary>
        public const string BarPrefix = "bar:";

        private readonly PortalData _portal;
        private readonly string _decalDirectory;
        private readonly string _virindiViewServicePath;
        private readonly ConcurrentDictionary<string, Lazy<Result>> _cache = new ConcurrentDictionary<string, Lazy<Result>>(StringComparer.OrdinalIgnoreCase);
        private readonly Lazy<ResourceList> _vvsResources;

        /// <param name="portal">The client's portal archive; null makes every <c>portal:</c> key fail. Not disposed here.</param>
        /// <param name="decalDirectory">Decal's install directory; null makes every <c>decal:</c> key fail.</param>
        /// <param name="virindiViewServicePath">VirindiViewService.dll; null makes every <c>vvs:</c> key fail.</param>
        public ImageCatalog(PortalData portal, string decalDirectory, string virindiViewServicePath)
        {
            _portal = portal;
            _decalDirectory = decalDirectory;
            _virindiViewServicePath = virindiViewServicePath;
            _vvsResources = new Lazy<ResourceList>(ListVirindiViewService, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public ImageCatalog(PortalData portal, DecalInstall install)
            : this(portal, install?.DecalDirectory, install?.VirindiViewServicePath)
        {
        }

        /// <summary>The keys that could not be resolved so far, each with the reason. A snapshot.</summary>
        public IReadOnlyDictionary<string, string> Failures
        {
            get
            {
                Dictionary<string, string> failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, Lazy<Result>> entry in _cache)
                {
                    if (entry.Value.IsValueCreated && entry.Value.Value.Error != null)
                        failures[entry.Key] = entry.Value.Value.Error;
                }

                return failures;
            }
        }

        /// <summary>How many keys have been asked for, found or not.</summary>
        public int Count => _cache.Count;

        /// <summary>
        /// The image a key names. Keys compare without regard to case, as file names and hex
        /// digits do on the machines this runs on.
        /// </summary>
        public bool TryGet(string key, out RgbaImage image, out string error)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                image = null;
                error = "no image key was given";
                return false;
            }

            Result result = _cache.GetOrAdd(key, k => new Lazy<Result>(() => Resolve(k), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
            image = result.Image;
            error = result.Error;
            return image != null;
        }

        private Result Resolve(string key)
        {
            try
            {
                if (key.StartsWith(PortalPrefix, StringComparison.OrdinalIgnoreCase))
                    return FromPortal(key.Substring(PortalPrefix.Length).Trim());

                if (key.StartsWith(VirindiViewServicePrefix, StringComparison.OrdinalIgnoreCase))
                    return FromVirindiViewService(key.Substring(VirindiViewServicePrefix.Length).Trim());

                if (key.StartsWith(DecalPrefix, StringComparison.OrdinalIgnoreCase))
                    return FromDecal(key.Substring(DecalPrefix.Length).Trim());

                if (key.StartsWith(BarPrefix, StringComparison.OrdinalIgnoreCase))
                    return FromBar(key.Substring(BarPrefix.Length).Trim());

                if (key.StartsWith(HostPrefix, StringComparison.OrdinalIgnoreCase))
                    return FromHost(key.Substring(HostPrefix.Length).Trim());

                return Result.Failed($"{key} does not start with {PortalPrefix}, {VirindiViewServicePrefix}, {DecalPrefix}, {BarPrefix} or {HostPrefix}");
            }
            catch (Exception ex)
            {
                // A resolver runs on a background thread for the sake of drawing; one bad image
                // must not take the thread down with it. The reason is kept for the log.
                return Result.Failed($"{key}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private Result FromPortal(string id)
        {
            if (_portal == null)
                return Result.Failed("the client's portal archive is not open");

            string digits = id.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? id.Substring(2) : id;

            if (digits.Length == 0 || digits.Length > 8
                || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint fileId))
            {
                return Result.Failed($"'{id}' is not a hex image id");
            }

            return _portal.TryGetImage(fileId, out RgbaImage image, out string error)
                ? Result.Found(image)
                : Result.Failed(error);
        }

        /// <summary>
        /// A resource matches when its name is the one given or ends with it after a dot, so
        /// the namespace can be left off but a key cannot match half of a word -
        /// <c>Left.png</c> does not find <c>TabActiveLeft.png</c>.
        /// </summary>
        private Result FromVirindiViewService(string name)
        {
            if (_virindiViewServicePath == null)
                return Result.Failed("Virindi View Service was not found, so its theme images are not available");

            if (name.Length == 0)
                return Result.Failed("no resource name follows vvs:");

            ResourceList resources = _vvsResources.Value;
            if (resources.Error != null)
                return Result.Failed(resources.Error);

            List<string> matches = new List<string>();
            foreach (string resource in resources.Names)
            {
                if (resource.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || (resource.EndsWith(name, StringComparison.OrdinalIgnoreCase) && resource[resource.Length - name.Length - 1] == '.'))
                {
                    matches.Add(resource);
                }
            }

            if (matches.Count == 0)
                return Result.Failed($"{Path.GetFileName(_virindiViewServicePath)} has no resource ending in {name}");

            if (matches.Count > 1)
                return Result.Failed($"{name} could be any of {string.Join(", ", matches)}");

            if (!ManifestResources.TryRead(_virindiViewServicePath, matches[0], out byte[] data, out string error))
                return Result.Failed(error);

            if (!ImageFiles.TryDecodePng(data, out RgbaImage image, out error))
                return Result.Failed($"{matches[0]}: {error}");

            return Result.Found(ImageFiles.WithColourKey(image, 0, 255, 255));
        }

        /// <summary>One of the images built into this assembly, by name without its extension.</summary>
        private static Result FromHost(string name)
        {
            if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\', ':', '.' }) >= 0)
                return Result.Failed($"'{name}' is not the name of an image the host carries");

            using System.IO.Stream stream = typeof(ImageCatalog).Assembly.GetManifestResourceStream("AC.Dat.Images." + name.ToLowerInvariant() + ".png");
            if (stream == null)
                return Result.Failed($"the host carries no image called '{name}'");

            using System.IO.MemoryStream copy = new System.IO.MemoryStream();
            stream.CopyTo(copy);
            return ImageFiles.TryDecodePng(copy.ToArray(), out RgbaImage image, out string error)
                ? Result.Found(image)
                : Result.Failed($"host:{name}: {error}");
        }

        private Result FromDecal(string fileName)
        {
            if (_decalDirectory == null)
                return Result.Failed("Decal's directory was not found, so its theme images are not available");

            // The key is only ever a file name. Anything that could climb out of Decal's
            // directory, or name a drive or a stream, is refused before the disk is touched.
            if (fileName.Length == 0
                || fileName.Contains("..", StringComparison.Ordinal)
                || fileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0
                || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || Path.IsPathRooted(fileName))
            {
                return Result.Failed($"'{fileName}' is not a plain file name");
            }

            string path = Path.Combine(_decalDirectory, fileName);
            if (!File.Exists(path))
                return Result.Failed($"{fileName} is not in {_decalDirectory}");

            byte[] data;
            try
            {
                data = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return Result.Failed($"{path} could not be read: {ex.Message}");
            }

            if (!ImageFiles.TryDecode(data, out RgbaImage image, out string error))
                return Result.Failed($"{fileName}: {error}");

            return Result.Found(ImageFiles.WithColourKey(image, 0, 255, 255));
        }

        private ResourceList ListVirindiViewService()
        {
            // Listed once, not per key: every theme image is a lookup in the same list.
            return ManifestResources.TryList(_virindiViewServicePath, out IReadOnlyList<string> names, out string error)
                ? new ResourceList(names, null)
                : new ResourceList(Array.Empty<string>(), error);
        }

        private sealed class ResourceList
        {
            public ResourceList(IReadOnlyList<string> names, string error)
            {
                Names = names;
                Error = error;
            }

            public IReadOnlyList<string> Names { get; }

            public string Error { get; }
        }

        /// <summary>
        /// Decal drew each plugin's switch on its bar by filling the switchbar template's pill
        /// with one of two textures: gold for a plugin whose window is open, red for one that
        /// is closed. The template carries the shape - cyan outside it - and its own shading,
        /// a darker rim round a light middle, so the texture is scaled by the template's
        /// brightness rather than pasted over it; pasted, the rim came out as a flat parchment
        /// outline that belonged to neither. The disabled switch is a finished bitmap already.
        /// </summary>
        private Result FromBar(string name)
        {
            string texture;
            switch (name.ToLowerInvariant())
            {
                case "open": texture = "Switch-Active.bmp"; break;
                case "closed": texture = "Switch-Inactive.bmp"; break;
                case "faulted":
                    return TryGet(DecalPrefix + "Switchbar Disabled.bmp", out RgbaImage disabled, out string why)
                        ? Result.Found(disabled)
                        : Result.Failed(why);
                default:
                    return Result.Failed($"there is no bar switch called {name}; there are open, closed and faulted");
            }

            if (!TryGet(DecalPrefix + "Switchbar Template.bmp", out RgbaImage template, out string templateError))
                return Result.Failed(templateError);

            if (!TryGet(DecalPrefix + texture, out RgbaImage fill, out string fillError))
                return Result.Failed(fillError);

            return Result.Found(ShapeLike(template, fill));
        }

        /// <summary>
        /// The template's shape, filled with the texture, shaded by the template's brightness.
        /// Pixels outside the shape are marked with the key colour and then keyed, so they are
        /// coloured like their neighbours and filtering at the edge does not fringe.
        /// </summary>
        internal static RgbaImage ShapeLike(RgbaImage template, RgbaImage fill)
        {
            // The template's light middle averages about this bright; a pixel that bright
            // takes the texture as it is, the rim proportionally darker.
            const double MiddleBrightness = 190.0;

            int w = template.Width;
            int h = template.Height;
            byte[] rgba = new byte[w * h * 4];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (template.Rgba[i + 3] == 0)
                    {
                        rgba[i] = 0;
                        rgba[i + 1] = 255;
                        rgba[i + 2] = 255;
                        rgba[i + 3] = 255;
                        continue;
                    }

                    double brightness = (template.Rgba[i] * 30 + template.Rgba[i + 1] * 59 + template.Rgba[i + 2] * 11) / 100.0;
                    double k = brightness / MiddleBrightness;

                    int j = ((y % fill.Height) * fill.Width + (x % fill.Width)) * 4;
                    rgba[i] = (byte)Math.Min(255, (int)(fill.Rgba[j] * k));
                    rgba[i + 1] = (byte)Math.Min(255, (int)(fill.Rgba[j + 1] * k));
                    rgba[i + 2] = (byte)Math.Min(255, (int)(fill.Rgba[j + 2] * k));
                    rgba[i + 3] = 255;
                }
            }

            return ImageFiles.WithColourKey(new RgbaImage(w, h, rgba), 0, 255, 255);
        }

        private sealed class Result
        {
            private Result(RgbaImage image, string error)
            {
                Image = image;
                Error = error;
            }

            public RgbaImage Image { get; }

            public string Error { get; }

            public static Result Found(RgbaImage image) => new Result(image, null);

            public static Result Failed(string error) => new Result(null, error ?? "the image could not be read, for no stated reason");
        }
    }
}
