using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using AC.Host.Plugins.Views;
using Decal.Adapter.Hosting;
using Microsoft.DirectX;
using VirindiViewService;
using VirindiViewService.Controls;
using Xunit;
using DxPlane = Microsoft.DirectX.Plane;
using DxVector2 = Microsoft.DirectX.Vector2;
using DxVector3 = Microsoft.DirectX.Vector3;

namespace AC.Host.Tests
{
    /// <summary>
    /// What plugins drew for themselves: VVS's textures and the controls drawn on them, which
    /// the overlay cannot show and the stand-ins take without harm; and Managed DirectX's
    /// maths, which plugins also work with, so it is real.
    /// </summary>
    public partial class DecalCompatTests
    {
        // ------------------------------------------------------------------- drawn by the plugin

        [Fact]
        public void ATextureIsItsSizeAndTakesEveryDrawingCallWithoutHarm()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);

            DxTexture marks = new DxTexture(new Size(20, 5));
            DxTexture surface = new DxTexture(new Size(300, 200));
            Assert.Equal(20, marks.Width);
            Assert.Equal(5, marks.Height);
            Assert.False(marks.IsDisposed);

            // Integrator2's map, frame by frame - with the theme's font, which here is none - and
            // some of it out of order, which VVS refused and which harms nothing here.
            surface.WriteText("before any text was begun", Color.White, WriteTextFormats.None, new Rectangle(0, 0, 10, 10));
            surface.BeginRender();
            surface.Fill(new Rectangle(0, 0, 300, 200), Color.Transparent);
            surface.DrawPortalImageNoBorder(0x06001E03, new Rectangle(0, 0, 16, 16));
            surface.DrawTextureTinted(marks, new Rectangle(5, 0, 5, 5), new Rectangle(40, 40, 5, 5), Color.Red.ToArgb());
            surface.DrawTextureWithTransform(marks, Matrix.Identity, -1);
            surface.DrawTextureWithTransform(marks, new Rectangle(0, 0, 5, 5), Matrix.Identity, -1);
            surface.DrawTextureRotated(marks, new Rectangle(0, 0, 20, 5), new Point(290, 10), -1, 0.5f);
            surface.DrawLines(new[] { new DxVector2(0, 0) }, new[] { new DxVector2(10, 10) }, Matrix.Identity, new[] { Color.Yellow }, 2f);
            surface.FlushSprite();
            surface.BeginText(null, 7f, 0, false, 0, 255);
            surface.WriteText("Holtburg", Color.White, Color.Black, WriteTextFormats.Bottom | WriteTextFormats.Center, new Rectangle(0, 185, 300, 15));
            surface.EndText();
            surface.EndRender();
            surface.Clear();
            surface.PopClipRect();

            Assert.Equal(Service.MeasureText("Holtburg", WriteTextFormats.None, "Arial", 12f, 400, false, 0),
                surface.MeasureText("Holtburg", WriteTextFormats.None, "Arial", 12, 400, false));

            marks.Dispose();
            marks.Dispose();
            Assert.True(marks.IsDisposed);
            Assert.Throws<ArgumentException>(() => new DxTexture(new Size(0, 5)));

            // Said once, whatever the number of textures.
            Assert.Single(host.Log.Lines, l => l.Contains("used DxTexture") && l.Contains("not shown"));
        }

        [Fact]
        public void AControlAPluginDrawsItselfIsAnEmptySpaceAndDrawsWhenThePluginAsks()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);

            // Built as Integrator2 builds its map: a subclass, its drawing hooked up, put in a page.
            HudView view = new HudView("VI2: Map", 316, 300, new ACImage(14200));
            HudFixedLayout page = new HudFixedLayout();
            view.Controls.HeadControl = page;
            Map map = new Map();
            page.AddControl(map, new Rectangle(16, 0, 300, 300));

            StaticText space = Assert.Single(runtime.Views).View.StaticText(map.Name);
            Assert.Equal(string.Empty, space.Text);
            Assert.Equal((16, 0, 300, 300), (space.Left, space.Top, space.Width, space.Height));
            Assert.Equal(new Rectangle(16, 0, 300, 300), map.DrawCommandRegion);
            Assert.Contains(host.Log.Lines, l => l.Contains("used HudEmulator"));

            // The host never raises the drawing; a plugin that asks gets it, as VVS gave it.
            Assert.Equal(0, map.Frames);
            DxTexture surface = new DxTexture(new Size(316, 300));
            map.DrawNow(surface);
            Assert.Equal(1, map.Frames);
            Assert.Same(surface, map.LastTarget);
            Assert.Equal(map.DrawCommandRegion, map.LastRegion);

            map.Visible = false;
            map.DrawNow(surface);
            Assert.Equal(1, map.Frames);

            map.Dispose();
            Assert.True(map.Disposed);
        }

        [Fact]
        public void NoWebBrowserIsOfferedAndOneMadeAnywayGoesNowhere()
        {
            MacroTestHost host = new MacroTestHost();
            using DecalRuntime runtime = new DecalRuntime(host);

            Assert.False(HudBrowser.IsAvailable);
            HudBrowser browser = new HudBrowser(400, 300);
            browser.TitleChanged += () => throw new InvalidOperationException("no page ever loads");
            browser.Navigate("http://www.virindi.net/wiki/index.php/Virindi_Plugins_FAQ");
            Assert.Equal(string.Empty, browser.Title);
            Assert.Contains(host.Log.Lines, l => l.Contains("used HudBrowser"));
        }

        // ------------------------------------------------------------------- Managed DirectX's maths

        [Fact]
        public void ManagedDirectXsMatricesWorkAsDirectXsDid()
        {
            DxVector2 Moved(float x, float y, Matrix m) => DxVector2.TransformCoordinate(new DxVector2(x, y), m);

            Matrix identity = Matrix.Identity;
            Assert.Equal((1f, 0f, 0f, 1f), (identity.M11, identity.M12, identity.M41, identity.M44));

            // Each maker replaces the matrix: a translation made from a zero matrix, as VVS made them.
            Matrix move = new Matrix();
            move.Translate(10, 20, 0);
            Assert.Equal((11f, 22f), Rounded(Moved(1, 2, move)));

            // Row vectors: a quarter turn about Z takes X to Y; a product acts left first.
            Matrix turn = Matrix.Identity;
            turn.RotateZ((float)Math.PI / 2);
            Assert.Equal((0f, 1f), Rounded(Moved(1, 0, turn), 5));

            Matrix scaleThenMove = Matrix.Identity;
            scaleThenMove.Scale(2, 2, 1);
            Matrix right = Matrix.Identity;
            right.Translate(10, 0, 0);
            scaleThenMove.Multiply(right);
            Assert.Equal((12f, 0f), Rounded(Moved(1, 0, scaleThenMove)));

            Matrix back = scaleThenMove;
            back.Invert();
            Assert.Equal((1f, 0f), Rounded(Moved(12, 0, back), 5));
            Matrix none = new Matrix();
            none.Invert();
            Assert.Equal(new Matrix(), none);

            // Integrator2 turns its map over with this: the plane y = 0, from three points on it.
            DxPlane ground = DxPlane.FromPoints(new DxVector3(0, 0, 0), new DxVector3(1, 0, 0), new DxVector3(0, 0, 1));
            Assert.Equal((0f, -1f, 0f, 0f), (ground.A, ground.B, ground.C, ground.D));
            Matrix mirror = Matrix.Identity;
            mirror.Reflect(ground);
            Assert.Equal((3f, -4f), Rounded(Moved(3, 4, mirror)));

            // A coordinate is divided back by w; the instance form moves the vector itself.
            Matrix halve = Matrix.Identity;
            halve.M44 = 2;
            DxVector2 point = new DxVector2(1, 2);
            point.TransformCoordinate(halve);
            Assert.Equal((0.5f, 1f), Rounded(point));
        }

        // ------------------------------------------------------------------- bound by signature

        /// <summary>
        /// Plugins bind to the stand-ins by name and signature, so each member has to be what VVS
        /// 1.0.0.47 and Managed DirectX 1.1 declared - a float where they had a float, a struct
        /// where they had a struct. These are the ones Integrator2 calls. Read from metadata, so
        /// that those taking a System.Drawing bitmap are checked too: this test process has only
        /// .NET's half of System.Drawing, without the bitmaps.
        /// </summary>
        [Fact]
        public void TheDrawingStandInsHaveTheSignaturesPluginsWereBuiltAgainst()
        {
            Assert.Equal(typeof(HudControl), typeof(HudEmulator).BaseType);
            Assert.Equal(typeof(HudControl), typeof(HudBrowser).BaseType);
            Assert.True(typeof(HudControl).GetMethod(nameof(HudControl.Dispose)).IsVirtual);
            Assert.True(typeof(HudControl).GetMethod(nameof(HudControl.DrawNow)).IsVirtual);
            Assert.All(new[] { typeof(Matrix), typeof(DxPlane), typeof(DxVector2), typeof(DxVector3) }, t => Assert.True(t.IsValueType, t.Name));
            Assert.Equal("Microsoft.DirectX", typeof(Matrix).Assembly.GetName().Name);
            Assert.Equal(new Version(1, 0, 2902, 0), typeof(Matrix).Assembly.GetName().Version);

            Assert.Subset(Signatures(typeof(DxTexture)), new HashSet<string>
            {
                ".ctor(System.Drawing.Size) : System.Void",
                ".ctor(System.Drawing.Bitmap) : System.Void",
                "get_Width() : System.Int32",
                "get_Height() : System.Int32",
                "get_IsDisposed() : System.Boolean",
                "Dispose() : System.Void",
                "BeginRender() : System.Void",
                "EndRender() : System.Void",
                "BeginText(System.String, System.Single, System.Int32, System.Boolean, System.Int32, System.Int32) : System.Void",
                "EndText() : System.Void",
                "WriteText(System.String, System.Drawing.Color, System.Drawing.Color, VirindiViewService.WriteTextFormats, System.Drawing.Rectangle) : System.Void",
                "Clear() : System.Void",
                "Fill(System.Drawing.Rectangle, System.Drawing.Color) : System.Void",
                "DrawImage(System.Drawing.Bitmap, System.Drawing.Rectangle, System.Drawing.Color) : System.Void",
                "DrawPortalImageNoBorder(System.Int32, System.Drawing.Rectangle) : System.Void",
                "DrawTexture(VirindiViewService.DxTexture, System.Drawing.Rectangle) : System.Void",
                "DrawTextureTinted(VirindiViewService.DxTexture, System.Drawing.Rectangle, System.Drawing.Rectangle, System.Int32) : System.Void",
                "DrawTextureWithTransform(VirindiViewService.DxTexture, Microsoft.DirectX.Matrix, System.Int32) : System.Void",
                "DrawTextureWithTransform(VirindiViewService.DxTexture, System.Drawing.Rectangle, Microsoft.DirectX.Matrix, System.Int32) : System.Void",
                "DrawTextureRotated(VirindiViewService.DxTexture, System.Drawing.Rectangle, System.Drawing.Point, System.Int32, System.Single) : System.Void",
                "DrawLines(Microsoft.DirectX.Vector2[], Microsoft.DirectX.Vector2[], Microsoft.DirectX.Matrix, System.Drawing.Color[], System.Single) : System.Void",
                "FlushSprite() : System.Void",
            });
            Assert.Subset(Signatures(typeof(HudEmulator)), new HashSet<string>
            {
                ".ctor() : System.Void",
                "add_Draw(VirindiViewService.Controls.HudEmulator/delDraw) : System.Void",
                "get_DrawCommandRegion() : System.Drawing.Rectangle",
                "set_LeaveSurfaceInBeginRender(System.Boolean) : System.Void",
            });
            Assert.Subset(Signatures(typeof(HudEmulator.delDraw)), new HashSet<string>
            {
                ".ctor(System.Object, System.IntPtr) : System.Void",
                "Invoke(VirindiViewService.Controls.HudEmulator, VirindiViewService.DxTexture, System.Drawing.Rectangle, VirindiViewService.Controls.HudEmulator/delClearRegion) : System.Void",
            });
            Assert.Subset(Signatures(typeof(HudEmulator.delClearRegion)), new HashSet<string> { "Invoke(VirindiViewService.DxTexture, System.Drawing.Rectangle) : System.Void" });
            Assert.Subset(Signatures(typeof(HudBrowser)), new HashSet<string>
            {
                ".ctor(System.Int32, System.Int32) : System.Void",
                "static get_IsAvailable() : System.Boolean",
                "add_TitleChanged(VirindiViewService.Controls.HudBrowser/delTC) : System.Void",
                "Navigate(System.String) : System.Void",
            });
            Assert.Subset(Signatures(typeof(Matrix)), new HashSet<string>
            {
                "static get_Identity() : Microsoft.DirectX.Matrix",
                "Invert() : System.Void",
                "Multiply(Microsoft.DirectX.Matrix) : System.Void",
                "Reflect(Microsoft.DirectX.Plane) : System.Void",
                "RotateX(System.Single) : System.Void",
                "RotateY(System.Single) : System.Void",
                "RotateZ(System.Single) : System.Void",
                "Scale(System.Single, System.Single, System.Single) : System.Void",
                "Translate(System.Single, System.Single, System.Single) : System.Void",
            });
            Assert.Subset(Signatures(typeof(DxPlane)), new HashSet<string> { "static FromPoints(Microsoft.DirectX.Vector3, Microsoft.DirectX.Vector3, Microsoft.DirectX.Vector3) : Microsoft.DirectX.Plane" });
            Assert.Subset(Signatures(typeof(DxVector2)), new HashSet<string>
            {
                "field X : System.Single",
                "field Y : System.Single",
                ".ctor(System.Single, System.Single) : System.Void",
                "TransformCoordinate(Microsoft.DirectX.Matrix) : System.Void",
                "static TransformCoordinate(Microsoft.DirectX.Vector2, Microsoft.DirectX.Matrix) : Microsoft.DirectX.Vector2",
            });
            Assert.Subset(Signatures(typeof(DxVector3)), new HashSet<string> { ".ctor(System.Single, System.Single, System.Single) : System.Void" });
        }

        /// <summary>A HudEmulator as plugins subclass it: drawing hooked up in its constructor, Dispose overridden.</summary>
        private sealed class Map : HudEmulator
        {
            public Map()
            {
                LeaveSurfaceInBeginRender = true;
                Draw += OnDraw;
            }

            public int Frames { get; private set; }

            public DxTexture LastTarget { get; private set; }

            public Rectangle LastRegion { get; private set; }

            public bool Disposed { get; private set; }

            public override void Dispose()
            {
                base.Dispose();
                Disposed = true;
            }

            private void OnDraw(HudEmulator caller, DxTexture target, Rectangle region, delClearRegion clear)
            {
                Assert.Same(this, caller);
                clear(target, region);
                Frames++;
                LastTarget = target;
                LastRegion = region;
            }
        }

        private static (float X, float Y) Rounded(DxVector2 v, int digits = 4)
            => ((float)Math.Round(v.X, digits), (float)Math.Round(v.Y, digits));

        /// <summary>
        /// A type's methods and fields as "static? Name(Param, Param) : Return" and "field Name :
        /// Type", every type by its full name - nested ones as Outer/Inner - read from the
        /// assembly's metadata, so that nothing a signature names has to be loaded.
        /// </summary>
        private static HashSet<string> Signatures(Type type)
        {
            using FileStream stream = File.OpenRead(type.Assembly.Location);
            using PEReader pe = new PEReader(stream);
            MetadataReader reader = pe.GetMetadataReader();
            TypeNames names = new TypeNames();
            string wanted = type.FullName.Replace('+', '/');

            TypeDefinition definition = reader.TypeDefinitions.Select(reader.GetTypeDefinition).Single(t => TypeNames.Name(reader, t) == wanted);
            HashSet<string> signatures = new HashSet<string>();
            foreach (MethodDefinitionHandle handle in definition.GetMethods())
            {
                MethodDefinition method = reader.GetMethodDefinition(handle);
                MethodSignature<string> signature = method.DecodeSignature(names, null);
                string prefix = signature.Header.IsInstance ? string.Empty : "static ";
                signatures.Add($"{prefix}{reader.GetString(method.Name)}({string.Join(", ", signature.ParameterTypes)}) : {signature.ReturnType}");
            }

            foreach (FieldDefinitionHandle handle in definition.GetFields())
            {
                FieldDefinition field = reader.GetFieldDefinition(handle);
                signatures.Add($"field {reader.GetString(field.Name)} : {field.DecodeSignature(names, null)}");
            }

            return signatures;
        }

        /// <summary>Type names out of signatures, as metadata spells them.</summary>
        private sealed class TypeNames : ISignatureTypeProvider<string, object>
        {
            public static string Name(MetadataReader reader, TypeDefinition type)
            {
                string name = reader.GetString(type.Name);
                return type.GetDeclaringType().IsNil
                    ? Qualified(reader.GetString(type.Namespace), name)
                    : Name(reader, reader.GetTypeDefinition(type.GetDeclaringType())) + "/" + name;
            }

            public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Name(reader, reader.GetTypeDefinition(handle));

            public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            {
                TypeReference type = reader.GetTypeReference(handle);
                string name = reader.GetString(type.Name);
                return type.ResolutionScope.Kind == HandleKind.TypeReference
                    ? GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, rawTypeKind) + "/" + name
                    : Qualified(reader.GetString(type.Namespace), name);
            }

            public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode switch
            {
                PrimitiveTypeCode.Void => "Void",
                PrimitiveTypeCode.Boolean => "Boolean",
                PrimitiveTypeCode.Int32 => "Int32",
                PrimitiveTypeCode.Single => "Single",
                PrimitiveTypeCode.String => "String",
                PrimitiveTypeCode.Object => "Object",
                PrimitiveTypeCode.IntPtr => "IntPtr",
                _ => typeCode.ToString(),
            };

            public string GetSZArrayType(string elementType) => elementType + "[]";

            public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

            public string GetByReferenceType(string elementType) => elementType + "&";

            public string GetPointerType(string elementType) => elementType + "*";

            public string GetPinnedType(string elementType) => elementType;

            public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

            public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(", ", typeArguments) + ">";

            public string GetGenericTypeParameter(object genericContext, int index) => "!" + index;

            public string GetGenericMethodParameter(object genericContext, int index) => "!!" + index;

            public string GetFunctionPointerType(MethodSignature<string> signature) => "method*";

            public string GetTypeFromSpecification(MetadataReader reader, object genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
                => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

            private static string Qualified(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
        }
    }
}
