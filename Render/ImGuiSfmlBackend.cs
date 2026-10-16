using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Evergine.Bindings.Imgui;
using Evergine.Mathematics;
using SFML.Graphics;
using SFML.System;
using SFML.Window;

using ImGuiVector2 = Evergine.Mathematics.Vector2;
using SfVector2f = SFML.System.Vector2f;

namespace RimClone.Render;

internal unsafe sealed class ImGuiSfmlBackend : IDisposable
{
    private readonly Dictionary<ulong, Texture> _textures = new();
    private readonly ushort[] _cyrillicRanges =
    {
        0x0020, 0x00FF, 0x0400, 0x052F, 0x2DE0, 0x2DFF, 0xA640, 0xA69F, 0
    };
    private GCHandle _glyphRangesPin;
    private IntPtr _context;
    private ulong _nextTextureId = 1;
    private Vertex[] _vertices = new Vertex[4096];
    private bool _disposed;

    public bool WantCaptureMouse => _context != IntPtr.Zero && GetIo()->WantCaptureMouse != 0;
    public bool WantCaptureKeyboard => _context != IntPtr.Zero && GetIo()->WantCaptureKeyboard != 0;

    public ImGuiSfmlBackend()
    {
        _context = ImguiNative.igCreateContext(null);
        if (_context == IntPtr.Zero)
            throw new InvalidOperationException("Dear ImGui did not create a context.");

        EnsureCurrent();
        ImGuiIO* io = ImguiNative.igGetIO_Nil();
        io->ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io->BackendFlags |= ImGuiBackendFlags.RendererHasTextures;
        io->IniFilename = null;
        io->LogFilename = null;
        io->Fonts->TexDesiredFormat = ImTextureFormat.RGBA32;
        ImguiNative.igStyleColorsDark(ImguiNative.igGetStyle());
        LoadEditorFont(io->Fonts);
    }

    private ImGuiIO* GetIo()
    {
        EnsureCurrent();
        return ImguiNative.igGetIO_Nil();
    }

    private void EnsureCurrent()
    {
        if (_context != IntPtr.Zero)
            ImguiNative.igSetCurrentContext(_context);
    }

    private void LoadEditorFont(ImFontAtlas* atlas)
    {
        string fontDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string[] paths =
        {
            Path.Combine(fontDirectory, "arial.ttf"),
            @"C:\Windows\Fonts\arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation2/LiberationSans-Regular.ttf"
        };
        _glyphRangesPin = GCHandle.Alloc(_cyrillicRanges, GCHandleType.Pinned);
        ushort* ranges = (ushort*)_glyphRangesPin.AddrOfPinnedObject();
        foreach (string path in paths)
        {
            if (!File.Exists(path))
                continue;
            if (ImguiNative.ImFontAtlas_AddFontFromFileTTF(atlas, path, 16f, null, ranges) != null)
                return;
        }
        ImguiNative.ImFontAtlas_AddFontDefault(atlas, null);
    }

    public void BeginFrame(uint width, uint height, float deltaSeconds)
    {
        EnsureCurrent();
        ImGuiIO* io = ImguiNative.igGetIO_Nil();
        io->DisplaySize = new ImGuiVector2(width, height);
        io->DisplayFramebufferScale = new ImGuiVector2(1f, 1f);
        io->DeltaTime = MathF.Max(0.001f, deltaSeconds);
        ImguiNative.igNewFrame();
    }

    public void MousePosition(float x, float y)
    {
        if (_context == IntPtr.Zero || _disposed) return;
        EnsureCurrent();
        ImguiNative.ImGuiIO_AddMousePosEvent(ImguiNative.igGetIO_Nil(), x, y);
    }

    public void MouseButton(Mouse.Button button, bool down)
    {
        if (_context == IntPtr.Zero || _disposed) return;
        int index = button switch
        {
            SFML.Window.Mouse.Button.Left => 0,
            SFML.Window.Mouse.Button.Right => 1,
            SFML.Window.Mouse.Button.Middle => 2,
            _ => -1
        };
        if (index < 0) return;
        EnsureCurrent();
        ImguiNative.ImGuiIO_AddMouseButtonEvent(ImguiNative.igGetIO_Nil(), index, down);
    }

    public void MouseWheel(float x, float y)
    {
        if (_context == IntPtr.Zero || _disposed) return;
        EnsureCurrent();
        ImguiNative.ImGuiIO_AddMouseWheelEvent(ImguiNative.igGetIO_Nil(), x, y);
    }

    public void TextEntered(string text)
    {
        if (_context == IntPtr.Zero || _disposed || string.IsNullOrEmpty(text)) return;
        EnsureCurrent();
        ImguiNative.ImGuiIO_AddInputCharactersUTF8(ImguiNative.igGetIO_Nil(), text);
    }

    public void KeyEvent(Keyboard.Key key, bool down)
    {
        if (_context == IntPtr.Zero || _disposed) return;
        ImGuiKey mapped = MapKey(key);
        if (mapped == ImGuiKey.None) return;
        EnsureCurrent();
        ImguiNative.ImGuiIO_AddKeyEvent(ImguiNative.igGetIO_Nil(), mapped, down);
    }

    private static ImGuiKey MapKey(Keyboard.Key key)
    {
        string name = key.ToString();
        if (name.Length == 1 && char.IsLetter(name[0]) && Enum.TryParse(name, true, out ImGuiKey letter))
            return letter;
        return name switch
        {
            "Num0" => ImGuiKey._0, "Num1" => ImGuiKey._1, "Num2" => ImGuiKey._2,
            "Num3" => ImGuiKey._3, "Num4" => ImGuiKey._4, "Num5" => ImGuiKey._5,
            "Num6" => ImGuiKey._6, "Num7" => ImGuiKey._7, "Num8" => ImGuiKey._8, "Num9" => ImGuiKey._9,
            "Left" => ImGuiKey.LeftArrow, "Right" => ImGuiKey.RightArrow,
            "Up" => ImGuiKey.UpArrow, "Down" => ImGuiKey.DownArrow,
            "LControl" => ImGuiKey.LeftCtrl, "RControl" => ImGuiKey.RightCtrl,
            "LShift" => ImGuiKey.LeftShift, "RShift" => ImGuiKey.RightShift,
            "LAlt" => ImGuiKey.LeftAlt, "RAlt" => ImGuiKey.RightAlt,
            "Backspace" => ImGuiKey.Backspace, "Return" => ImGuiKey.Enter, "Enter" => ImGuiKey.Enter,
            "PageUp" => ImGuiKey.PageUp, "PageDown" => ImGuiKey.PageDown,
            "Escape" => ImGuiKey.Escape, "Delete" => ImGuiKey.Delete, "Insert" => ImGuiKey.Insert,
            "Home" => ImGuiKey.Home, "End" => ImGuiKey.End, "Space" => ImGuiKey.Space,
            _ when Enum.TryParse(name, true, out ImGuiKey parsed) => parsed,
            _ => ImGuiKey.None
        };
    }

    public void Render(RenderWindow target)
    {
        if (_context == IntPtr.Zero || _disposed) return;
        EnsureCurrent();
        ImguiNative.igRender();
        ImDrawData* drawData = ImguiNative.igGetDrawData();
        if (drawData == null || drawData->Valid == 0) return;
        UpdateTextures(drawData);
        RenderDrawData(target, drawData);
    }

    private void UpdateTextures(ImDrawData* drawData)
    {
        if (drawData->Textures == null) return;
        ImVector* list = drawData->Textures;
        ImTextureData** items = (ImTextureData**)list->Data;
        for (int i = 0; i < list->Size; i++)
        {
            ImTextureData* item = items[i];
            if (item == null) continue;
            switch (item->Status)
            {
                case ImTextureStatus.WantCreate:
                {
                    if (item->Width <= 0 || item->Height <= 0)
                    {
                        ImguiNative.ImTextureData_SetStatus(item, ImTextureStatus.Destroyed);
                        break;
                    }
                    var texture = new Texture(new Vector2u((uint)item->Width, (uint)item->Height))
                    {
                        Smooth = false,
                        Repeated = false
                    };
                    texture.Update(CopyPixels(item));
                    ulong id = _nextTextureId++;
                    _textures[id] = texture;
                    ImguiNative.ImTextureData_SetTexID(item, id);
                    ImguiNative.ImTextureData_SetStatus(item, ImTextureStatus.OK);
                    break;
                }
                case ImTextureStatus.WantUpdates:
                    if (_textures.TryGetValue(item->TexID, out Texture? updateTexture))
                    {
                        updateTexture.Update(CopyPixels(item));
                        ImguiNative.ImTextureData_SetStatus(item, ImTextureStatus.OK);
                    }
                    break;
                case ImTextureStatus.WantDestroy:
                    if (_textures.Remove(item->TexID, out Texture? destroyTexture))
                        destroyTexture.Dispose();
                    ImguiNative.ImTextureData_SetTexID(item, 0);
                    ImguiNative.ImTextureData_SetStatus(item, ImTextureStatus.Destroyed);
                    break;
            }
        }
    }

    private static byte[] CopyPixels(ImTextureData* data)
    {
        int width = data->Width;
        int height = data->Height;
        int bpp = Math.Max(1, data->BytesPerPixel);
        int pitch = ImguiNative.ImTextureData_GetPitch(data);
        byte* source = (byte*)ImguiNative.ImTextureData_GetPixels(data);
        var pixels = new byte[checked(width * height * 4)];
        if (source == null) return pixels;
        for (int y = 0; y < height; y++)
        {
            byte* row = source + y * pitch;
            int offset = y * width * 4;
            if (bpp >= 4)
                Marshal.Copy((IntPtr)row, pixels, offset, width * 4);
            else
                for (int x = 0; x < width; x++)
                {
                    int target = offset + x * 4;
                    pixels[target] = pixels[target + 1] = pixels[target + 2] = 255;
                    pixels[target + 3] = row[x];
                }
        }
        return pixels;
    }

    private void RenderDrawData(RenderWindow target, ImDrawData* data)
    {
        ImVector lists = data->CmdLists;
        if (lists.Size <= 0 || lists.Data == IntPtr.Zero) return;
        ImDrawList** drawLists = (ImDrawList**)lists.Data;
        View originalView = target.GetView();
        View clippedView = new(originalView);
        try
        {
            Vector2u size = target.Size;
            for (int li = 0; li < lists.Size; li++)
            {
                ImDrawList* drawList = drawLists[li];
                if (drawList == null) continue;
                ImDrawCmd* commands = (ImDrawCmd*)drawList->CmdBuffer.Data;
                ImDrawVert* sourceVertices = (ImDrawVert*)drawList->VtxBuffer.Data;
                ushort* indices = (ushort*)drawList->IdxBuffer.Data;
                for (int ci = 0; ci < drawList->CmdBuffer.Size; ci++)
                {
                    ImDrawCmd* command = &commands[ci];
                    if (command->ElemCount == 0 || command->UserCallback != IntPtr.Zero) continue;
                    float left = (command->ClipRect.X - data->DisplayPos.X) * data->FramebufferScale.X;
                    float top = (command->ClipRect.Y - data->DisplayPos.Y) * data->FramebufferScale.Y;
                    float right = (command->ClipRect.Z - data->DisplayPos.X) * data->FramebufferScale.X;
                    float bottom = (command->ClipRect.W - data->DisplayPos.Y) * data->FramebufferScale.Y;
                    if (right <= left || bottom <= top ||
                        !ApplyClipView(target, originalView, clippedView, size, left, top, right, bottom))
                        continue;

                    ulong texId = ImguiNative.ImDrawCmd_GetTexID(command);
                    if (!_textures.TryGetValue(texId, out Texture? texture)) continue;
                    int count = checked((int)command->ElemCount);
                    if (_vertices.Length < count) Array.Resize(ref _vertices, Math.Max(count, _vertices.Length * 2));
                    for (int i = 0; i < count; i++)
                    {
                        uint index = indices[checked((int)command->IdxOffset + i)];
                        ImDrawVert v = sourceVertices[checked((int)(command->VtxOffset + index))];
                        uint col = v.col;
                        _vertices[i] = new Vertex(
                            new SfVector2f(v.pos.X, v.pos.Y),
                            new Color((byte)col, (byte)(col >> 8), (byte)(col >> 16), (byte)(col >> 24)),
                            new SfVector2f(v.uv.X * texture.Size.X, v.uv.Y * texture.Size.Y));
                    }
                    target.SetView(clippedView);
                    target.Draw(_vertices, 0, (uint)count, PrimitiveType.Triangles, new RenderStates(texture));
                }
            }
        }
        finally
        {
            target.SetView(originalView);
            clippedView.Dispose();
            originalView.Dispose();
        }
    }

    private static bool ApplyClipView(
        RenderWindow target, View original, View clipped, Vector2u windowSize,
        float left, float top, float right, float bottom)
    {
        if (windowSize.X == 0 || windowSize.Y == 0) return false;
        Vector2i a = target.MapCoordsToPixel(new SfVector2f(left, top), original);
        Vector2i b = target.MapCoordsToPixel(new SfVector2f(right, bottom), original);
        float x0 = Math.Clamp(MathF.Min(a.X, b.X), 0f, windowSize.X);
        float y0 = Math.Clamp(MathF.Min(a.Y, b.Y), 0f, windowSize.Y);
        float x1 = Math.Clamp(MathF.Max(a.X, b.X), 0f, windowSize.X);
        float y1 = Math.Clamp(MathF.Max(a.Y, b.Y), 0f, windowSize.Y);
        float w = x1 - x0, h = y1 - y0;
        if (w < 1f || h < 1f) return false;
        FloatRect viewport = original.Viewport;
        float vx = viewport.Left * windowSize.X, vy = viewport.Top * windowSize.Y;
        float vw = viewport.Width * windowSize.X, vh = viewport.Height * windowSize.Y;
        if (vw < 1f || vh < 1f) return false;
        float cx = (x0 + x1) * 0.5f, cy = (y0 + y1) * 0.5f;
        float baseX = vx + vw * 0.5f, baseY = vy + vh * 0.5f;
        float worldX = original.Center.X + (cx - baseX) / vw * original.Size.X;
        float worldY = original.Center.Y + (cy - baseY) / vh * original.Size.Y;
        float worldW = original.Size.X * w / vw, worldH = original.Size.Y * h / vh;
        clipped.Center = new SfVector2f(worldX, worldY);
        clipped.Size = new SfVector2f(MathF.Max(0.01f, worldW), MathF.Max(0.01f, worldH));
        clipped.Viewport = new FloatRect(new SfVector2f(x0 / windowSize.X, y0 / windowSize.Y),
            new SfVector2f(w / windowSize.X, h / windowSize.Y));
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (Texture texture in _textures.Values) texture.Dispose();
        _textures.Clear();
        if (_context != IntPtr.Zero)
        {
            ImguiNative.igSetCurrentContext(_context);
            ImguiNative.igDestroyContext(_context);
            _context = IntPtr.Zero;
        }
        if (_glyphRangesPin.IsAllocated) _glyphRangesPin.Free();
        GC.SuppressFinalize(this);
    }
}
