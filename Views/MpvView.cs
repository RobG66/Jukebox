using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Threading;
using Jukebox.Mpv;

namespace Jukebox.Views;

/// <summary>
/// Avalonia control that renders MPV video into the OpenGL context.
/// Subclasses <see cref="OpenGlControlBase"/> — no native HWND, no airspace.
/// Side panels and transport bar are normal XAML siblings that render on
/// top via Z-order.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="MpvContext"/> (mpv handle + playback control) is owned by
/// the ViewModel. This control only manages the render context lifecycle:
/// creates it in <see cref="OnOpenGlInit"/>, renders frames in
/// <see cref="OnOpenGlRender"/>, and frees it in <see cref="OnOpenGlDeinit"/>.
/// </para>
/// <para>
/// <b>Render flow:</b>
/// <list type="number">
///   <item>MPV decodes video on its internal threads.</item>
///   <item>When a new frame is ready, MPV calls the update callback
///   (registered via <c>mpv_render_context_set_update_callback</c>).</item>
///   <item>The callback (which must NOT call mpv APIs) schedules a render
///   on the UI thread via <see cref="OpenGlControlBase.RequestNextFrameRendering"/>.</item>
///   <item>Avalonia calls <see cref="OnOpenGlRender"/> on the GL thread —
///   we call <see cref="MpvContext.Render(int, int, int)"/> to render the
///   frame into Avalonia's FBO.</item>
/// </list>
/// </para>
/// </remarks>
public class MpvView : OpenGlControlBase
{
    /// <summary>
    /// Styled property for the <see cref="MpvContext"/> that this view
    /// renders. Set this to the VM's MPV context to connect the video
    /// output.
    /// </summary>
    public static readonly StyledProperty<MpvContext?> MpvContextProperty =
        AvaloniaProperty.Register<MpvView, MpvContext?>(nameof(MpvContext));

    public MpvContext? MpvContext
    {
        get => GetValue(MpvContextProperty);
        set => SetValue(MpvContextProperty, value);
    }

    // The update callback delegate — MUST be kept alive for the lifetime
    // of the render context. libmpv stores a raw function pointer; if the
    // delegate is GC'd, the next callback crashes.
    private MpvUpdateCallback? _updateCallback;
    private bool _renderContextCreated;

    // Delegate type for the render update callback.
    private delegate void MpvUpdateCallback(IntPtr ctx);

    static MpvView()
    {
        // Re-render when the MpvContext property changes.
        MpvContextProperty.Changed.AddClassHandler<MpvView>((view, _) => view.OnMpvContextChanged());
    }

    private void OnMpvContextChanged()
    {
        // If the GL context is already initialized and a new MpvContext is
        // set, we need to create the render context. Otherwise, it'll be
        // created in OnOpenGlInit.
        // For simplicity, we just request a re-render — the render context
        // is created lazily in OnOpenGlRender if not already created.
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        // Nothing to do here — the render context is created lazily in
        // OnOpenGlRender when we have both the GL interface and the
        // MpvContext. This avoids ordering issues if the MpvContext is
        // set after the control is loaded.
    }

    protected override void OnOpenGlRender(GlInterface gl, int fbo)
    {
        var ctx = MpvContext;
        if (ctx == null || ctx.Handle == IntPtr.Zero) return;

        // Skip rendering until the control has real dimensions.
        if (Bounds.Width < 1 || Bounds.Height < 1)
        {
            Dispatcher.UIThread.Post(() => RequestNextFrameRendering(),
                Avalonia.Threading.DispatcherPriority.Background);
            return;
        }

        // Create the render context if not already created.
        if (ctx.RenderContextHandle == IntPtr.Zero && !_renderContextCreated)
        {
            CreateRenderContext(gl, ctx);
        }

        if (!_renderContextCreated) return;
        if (ctx.RenderContextHandle == IntPtr.Zero) return;

        var size = GetPixelSize();
        try
        {
            ctx.Render(fbo, size.Width, size.Height);
        }
        catch (AccessViolationException)
        {
            // Render context was freed mid-call — suppress during shutdown.
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderContextCreated = false;
        _updateCallback = null;
    }

    private void CreateRenderContext(GlInterface gl, MpvContext ctx)
    {
        try
        {
            // Set up the get_proc_address delegate — MPV uses this to
            // resolve all OpenGL function pointers.
            ctx.GetProcAddressDelegate = (_, name) => gl.GetProcAddress(name);

            // Build the parameter array:
            // [0] MPV_RENDER_PARAM_API_TYPE → "opengl"
            // [1] MPV_RENDER_PARAM_OPENGL_INIT_PARAMS → mpv_opengl_init_params
            // [2] MPV_RENDER_PARAM_INVALID → terminator
            var apiTypePtr = Marshal.StringToHGlobalAnsi(MpvNative.MPV_RENDER_API_TYPE_OPENGL);
            var initParams = new MpvContext.MpvOpenglInitParams
            {
                GetProcAddress = ctx.GetProcAddressDelegate,
                GetProcAddressCtx = IntPtr.Zero
            };
            var initParamsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<MpvContext.MpvOpenglInitParams>());
            Marshal.StructureToPtr(initParams, initParamsPtr, false);

            var paramSize = Marshal.SizeOf<MpvContext.MpvRenderParam>();
            var paramsPtr = Marshal.AllocHGlobal(paramSize * 3);
            try
            {
                // param[0]: API type
                Marshal.WriteInt32(paramsPtr + 0 * paramSize, MpvNative.MPV_RENDER_PARAM_API_TYPE);
                Marshal.WriteIntPtr(paramsPtr + 0 * paramSize + IntPtr.Size, apiTypePtr);

                // param[1]: OpenGL init params
                Marshal.WriteInt32(paramsPtr + 1 * paramSize, MpvNative.MPV_RENDER_PARAM_OPENGL_INIT_PARAMS);
                Marshal.WriteIntPtr(paramsPtr + 1 * paramSize + IntPtr.Size, initParamsPtr);

                // param[2]: Invalid (terminator)
                Marshal.WriteInt32(paramsPtr + 2 * paramSize, MpvNative.MPV_RENDER_PARAM_INVALID);
                Marshal.WriteIntPtr(paramsPtr + 2 * paramSize + IntPtr.Size, IntPtr.Zero);

                int result = MpvNative.mpv_render_context_create(out var renderCtx, ctx.Handle, paramsPtr);
                if (result < 0)
                {
                    System.Diagnostics.Debug.WriteLine($"[MpvView] mpv_render_context_create failed with error {result}.");
                    return;
                }

                ctx.SetRenderContext(renderCtx);
                _renderContextCreated = true;

                // Set the update callback — MPV calls this when a new frame
                // is ready. The callback must NOT call any mpv API; it just
                // schedules a render on the UI thread.
                _updateCallback = OnRenderUpdate;
                var callbackPtr = Marshal.GetFunctionPointerForDelegate(_updateCallback);
                MpvNative.mpv_render_context_set_update_callback(renderCtx, callbackPtr, IntPtr.Zero);

                // Signal that the render context is ready — unblocks
                // PlayVideoAsync's WaitForRenderContextReadyAsync call.
                // This prevents the "first video is black" race condition
                // where MPV starts decoding before the render surface exists.
                ctx.MarkRenderContextReady();

                System.Diagnostics.Debug.WriteLine("[MpvView] Render context created successfully.");
            }
            finally
            {
                Marshal.FreeHGlobal(paramsPtr);
                Marshal.FreeHGlobal(initParamsPtr);
                Marshal.FreeHGlobal(apiTypePtr);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MpvView] CreateRenderContext failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Called by MPV (from an arbitrary thread) when a new frame is ready.
    /// Must NOT call any mpv API — just schedule a render on the UI thread.
    /// </summary>
    private void OnRenderUpdate(IntPtr ctx)
    {
        Dispatcher.UIThread.Post(() => RequestNextFrameRendering());
    }

    private PixelSize GetPixelSize()
    {
        // Get the display scaling from the TopLevel. On a 150% DPI display,
        // this returns 1.5 — we multiply the control's DIP bounds to get
        // pixel dimensions for the GL framebuffer.
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

        return new PixelSize(
            Math.Max(1, (int)(Bounds.Width * scale)),
            Math.Max(1, (int)(Bounds.Height * scale)));
    }
}
