using System.Numerics;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SiegeFX.Core.Actors;
using SiegeFX.Core.Assets;
using SiegeFX.Core.Sfx;
using SiegeFX.Core.Skrit;
using SiegeFX.Core.Tank;
using SiegeFX.Runtime.Render.Hud;

namespace SiegeFX.Runtime.Render;

public sealed partial class RenderHost
{
    /// <summary>SC-VIEW-ROTATE — render the full equipped character to the
    /// 256² view target at the current drag yaw. Same studio-lit FBO recipe
    /// as the portrait (body subsets + worn gear layers), framed head-to-toe
    /// with the camera orbiting the character. Called once per frame while
    /// the View button is held; state restored for the rest of the HUD.</summary>
    private unsafe void RenderPaperdollView()
    {
        if (_gl is null || _skinShader is null || _player is null || _window is null) return;
        var mesh = _player.Actor.Mesh;
        if (mesh.BoneCount == 0) return;

        if (_pdViewFbo == 0)
        {
            _pdViewFboColor = _gl.GenTexture();
            _gl.BindTexture(GLEnum.Texture2D, _pdViewFboColor);
            _gl.TexImage2D(GLEnum.Texture2D, 0, (int)GLEnum.Rgba8,
                           PdViewPx, PdViewPx, 0, GLEnum.Rgba, GLEnum.UnsignedByte, null);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Linear);
            _pdViewFboDepth = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(GLEnum.Renderbuffer, _pdViewFboDepth);
            _gl.RenderbufferStorage(GLEnum.Renderbuffer, GLEnum.DepthComponent24, PdViewPx, PdViewPx);
            _pdViewFbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(GLEnum.Framebuffer, _pdViewFbo);
            _gl.FramebufferTexture2D(GLEnum.Framebuffer, GLEnum.ColorAttachment0,
                                     GLEnum.Texture2D, _pdViewFboColor, 0);
            _gl.FramebufferRenderbuffer(GLEnum.Framebuffer, GLEnum.DepthAttachment,
                                        GLEnum.Renderbuffer, _pdViewFboDepth);
            if (_gl.CheckFramebufferStatus(GLEnum.Framebuffer) != GLEnum.FramebufferComplete)
            {
                Console.WriteLine("[view] FBO incomplete — View rotate disabled");
                _gl.BindFramebuffer(GLEnum.Framebuffer, 0);
                _gl.DeleteFramebuffer(_pdViewFbo); _pdViewFbo = 0;
                _pdViewActive = false;
                return;
            }
        }
        else _gl.BindFramebuffer(GLEnum.Framebuffer, _pdViewFbo);

        _gl.Viewport(0, 0, PdViewPx, PdViewPx);
        _gl.ClearColor(0.055f, 0.05f, 0.045f, 1f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        _gl.Enable(EnableCap.DepthTest);
        _gl.Disable(GLEnum.CullFace);

        // Pose: the live clip/time, same as the portrait.
        var clips = _player.Actor.Clips;
        SiegeFX.Core.Assets.PrsAnimation? clip = null;
        float t = 0f;
        if (clips.Length > 0)
        {
            int cidx = Math.Min(_player.Actor.CurrentClipIndex, clips.Length - 1);
            clip = clips[cidx];
            t = clip.AnimLength > 0f ? (float)(_player.AnimTime % clip.AnimLength) : 0f;
        }
        int bones = mesh.BoneCount;
        if (_boneWorldsScratch.Length < bones)
            _boneWorldsScratch = new Matrix4x4[Math.Max(bones, 64)];
        AnimationRuntime.ComputeAnimatedBoneWorlds(mesh, clip, t, _boneWorldsScratch);

        // Full-body orbit frame: feet at the actor origin, crown from the
        // posed head bone (+ margin); the eye circles at the drag yaw
        // starting FRONTAL (yaw 0 looks along the character's facing).
        int headIdx = -1;
        for (int i = 0; i < mesh.BoneNames.Count; i++)
            if (mesh.BoneNames[i].EndsWith("bip01_head", StringComparison.OrdinalIgnoreCase))
            { headIdx = i; break; }
        var headMesh = headIdx >= 0 ? _boneWorldsScratch[headIdx].Translation
                                    : new Vector3(0f, 1.7f, 0f);
        var feet = _player.CurrentTransform.Translation;
        var headWorld = Vector3.Transform(headMesh, _player.CurrentTransform);
        float height = MathF.Max(1.4f, headWorld.Y - feet.Y + 0.35f);
        var face = _playerFacing.LengthSquared() > 0.01f
            ? Vector3.Normalize(new Vector3(_playerFacing.X, 0f, _playerFacing.Z))
            : Vector3.UnitZ;
        float cy = MathF.Cos(_pdViewYaw), sy = MathF.Sin(_pdViewYaw);
        var dir = new Vector3(face.X * cy - face.Z * sy, 0f, face.X * sy + face.Z * cy);
        var at  = feet + new Vector3(0f, height * 0.48f, 0f);
        var eye = at + dir * (height * 1.55f);
        var pvp = Matrix4x4.CreateLookAt(eye, at, Vector3.UnitY)
                * Matrix4x4.CreatePerspectiveFieldOfView(0.62f, 1f, 0.05f, 50f);

        _skinShader.Use();
        _skinShader.SetMatrix4("uViewProj", pvp);
        _skinShader.SetMatrix4("uModel", _player.CurrentTransform);
        _skinShader.SetInt("uAlbedo", 0);
        _skinShader.SetInt("uFlipV", 0);
        _skinShader.SetInt("uDirCount", 1);
        _skinShader.SetFloat("uAmbient", 0.52f);
        Span<Vector3> vdir = stackalloc Vector3[1];
        vdir[0] = Vector3.Normalize(at - eye);
        Span<Vector3> vcol = stackalloc Vector3[1];
        vcol[0] = new Vector3(0.85f, 0.82f, 0.75f);
        _skinShader.SetVec3Array("uDirDir", vdir);
        _skinShader.SetVec3Array("uDirColor", vcol);
        _skinShader.SetInt("uPointCount", 0);
        _skinShader.SetInt("uUseBakedLight", 0);

        if (_skinScratch.Length < bones)
            _skinScratch = new Matrix4x4[Math.Max(bones, 64)];
        if (clip is not null)
            AnimationRuntime.ComputeSkinMatrices(mesh, clip, t, _skinScratch);
        else
            for (int i = 0; i < bones; i++) _skinScratch[i] = Matrix4x4.Identity;
        _skinShader.SetMatrix4Array("uBones[0]", _skinScratch.AsSpan(0, bones));

        var subsets = mesh.Subsets;
        if (subsets.Length == 0)
        {
            BindActorSlot(_player.Actor, 0);
            _player.GlMesh.Draw();
        }
        else
        {
            int lastSlot = -1;
            foreach (var sub in subsets)
            {
                if (sub.TextureIndex != lastSlot)
                {
                    BindActorSlot(_player.Actor, sub.TextureIndex);
                    lastSlot = sub.TextureIndex;
                }
                _player.GlMesh.DrawSubset(sub.FirstTriangle, sub.TriangleCount);
            }
        }
        foreach (var layer in _equippedLayers)
        {
            int lb = layer.Asp.BoneCount;
            if (lb == 0 || lb > SkinnedMesh.MaxBones) continue;
            if (_skinScratch.Length < lb) _skinScratch = new Matrix4x4[Math.Max(lb, 64)];
            if (clip is not null)
                AnimationRuntime.ComputeSkinMatrices(layer.Asp, clip, t, _skinScratch);
            else
                for (int i = 0; i < lb; i++) _skinScratch[i] = Matrix4x4.Identity;
            _skinShader.SetMatrix4Array("uBones[0]", _skinScratch.AsSpan(0, lb));
            if (layer.Texture is not null)
            {
                layer.Texture.Bind(TextureUnit.Texture0);
                _skinShader.SetInt("uHasTexture", 1);
            }
            else _skinShader.SetInt("uHasTexture", 0);
            var lsub = layer.Asp.Subsets;
            if (lsub.Length == 0) layer.Mesh.Draw();
            else foreach (var sub in lsub) layer.Mesh.DrawSubset(sub.FirstTriangle, sub.TriangleCount);
        }

        var rgba = new byte[PdViewPx * PdViewPx * 4];
        fixed (byte* p = rgba)
            _gl.ReadPixels(0, 0, PdViewPx, PdViewPx, GLEnum.Rgba, GLEnum.UnsignedByte, p);
        _pdViewTex?.Dispose();
        _pdViewTex = new GlTexture(_gl, rgba, PdViewPx, PdViewPx, nearestFilter: false);

        _gl.Enable(GLEnum.CullFace);
        _gl.BindFramebuffer(GLEnum.Framebuffer, 0);
        var vfb = _window.FramebufferSize;
        _gl.Viewport(0, 0, (uint)vfb.X, (uint)vfb.Y);
        _skinLightingStamp = ulong.MaxValue;
    }

    /// <summary>Render the player's head-and-shoulders to a small offscreen
    /// target using the authored [portrait_camera] frame and rebuild
    /// <see cref="_playerPortraitLive"/> from the pixels. Runs between frames
    /// on the GL thread; scene lighting is swapped for a fixed studio key so
    /// a dark crypt doesn't produce a black portrait, then the lighting
    /// stamp is invalidated so the world pass re-uploads its own values.</summary>
    private unsafe void RenderPlayerPortrait()
    {
        if (_gl is null || _skinShader is null || _player is null) return;
        var mesh = _player.Actor.Mesh;
        if (mesh.BoneCount == 0) return;

        // Lazy FBO: color texture + depth renderbuffer at PortraitPx².
        if (_portraitFbo == 0)
        {
            _portraitFboColor = _gl.GenTexture();
            _gl.BindTexture(GLEnum.Texture2D, _portraitFboColor);
            _gl.TexImage2D(GLEnum.Texture2D, 0, (int)GLEnum.Rgba8,
                           PortraitPx, PortraitPx, 0, GLEnum.Rgba, GLEnum.UnsignedByte, null);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(GLEnum.Texture2D, GLEnum.TextureMagFilter, (int)GLEnum.Linear);
            _portraitFboDepth = _gl.GenRenderbuffer();
            _gl.BindRenderbuffer(GLEnum.Renderbuffer, _portraitFboDepth);
            _gl.RenderbufferStorage(GLEnum.Renderbuffer, GLEnum.DepthComponent24,
                                    PortraitPx, PortraitPx);
            _portraitFbo = _gl.GenFramebuffer();
            _gl.BindFramebuffer(GLEnum.Framebuffer, _portraitFbo);
            _gl.FramebufferTexture2D(GLEnum.Framebuffer, GLEnum.ColorAttachment0,
                                     GLEnum.Texture2D, _portraitFboColor, 0);
            _gl.FramebufferRenderbuffer(GLEnum.Framebuffer, GLEnum.DepthAttachment,
                                        GLEnum.Renderbuffer, _portraitFboDepth);
            if (_gl.CheckFramebufferStatus(GLEnum.Framebuffer) != GLEnum.FramebufferComplete)
            {
                Console.WriteLine("[portrait] FBO incomplete — live portrait disabled");
                _gl.BindFramebuffer(GLEnum.Framebuffer, 0);
                _gl.DeleteFramebuffer(_portraitFbo); _portraitFbo = 0;
                return;
            }
        }
        else _gl.BindFramebuffer(GLEnum.Framebuffer, _portraitFbo);

        _gl.Viewport(0, 0, PortraitPx, PortraitPx);
        // Opaque dark backdrop — the cell reads as a framed bust, and a failed
        // draw is detectable (brightness guard below) instead of black-on-black.
        _gl.ClearColor(0.055f, 0.05f, 0.045f, 1f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));
        _gl.Enable(EnableCap.DepthTest);
        // Regression fix: the first cut built its camera from raw head-BONE
        // axes (authored up = bone X). That view mirrored the winding, and
        // with global backface culling on, every triangle culled → a black
        // portrait. World-space camera + culling off for the pass is robust
        // for any pose/skeleton.
        _gl.Disable(GLEnum.CullFace);

        // Pose: the body's current clip/time (same pick as the main loop).
        var clips = _player.Actor.Clips;
        SiegeFX.Core.Assets.PrsAnimation? clip = null;
        float t = 0f;
        if (clips.Length > 0)
        {
            int cidx = Math.Min(_player.Actor.CurrentClipIndex, clips.Length - 1);
            clip = clips[cidx];
            t = clip.AnimLength > 0f ? (float)(_player.AnimTime % clip.AnimLength) : 0f;
        }
        int bones = mesh.BoneCount;
        if (_boneWorldsScratch.Length < bones)
            _boneWorldsScratch = new Matrix4x4[Math.Max(bones, 64)];
        AnimationRuntime.ComputeAnimatedBoneWorlds(mesh, clip, t, _boneWorldsScratch);

        // Camera: the authored [portrait_camera] intent (character_camera.gas
        // frames the head from ~3u in front of the face) executed in WORLD
        // space with the world up-axis — the same conventions as the scene
        // camera, so handedness/winding can never flip. Head position comes
        // from the posed bone, facing from the player's live heading.
        int headIdx = -1;
        for (int i = 0; i < mesh.BoneNames.Count; i++)
            if (mesh.BoneNames[i].EndsWith("bip01_head", StringComparison.OrdinalIgnoreCase))
            { headIdx = i; break; }
        var headMesh = headIdx >= 0 ? _boneWorldsScratch[headIdx].Translation
                                    : new Vector3(0f, 1.7f, 0f);
        var headWorld = Vector3.Transform(headMesh, _player.CurrentTransform);
        var face = _playerFacing.LengthSquared() > 0.01f
            ? Vector3.Normalize(new Vector3(_playerFacing.X, 0f, _playerFacing.Z))
            : Vector3.UnitZ;
        // Tight FACE crop: same ~1.15u standoff (close-in would fisheye the
        // nose) but a telephoto FOV so the head fills the 128² cell — the
        // old 0.50 rad framed a whole bust with visible shoulders. Aim rides
        // a touch above the head bone (neck pivot) to center the face.
        var eye = headWorld + face * 1.15f + new Vector3(0f, 0.14f, 0f);
        var at  = headWorld + new Vector3(0f, 0.08f, 0f);
        var pvp = Matrix4x4.CreateLookAt(eye, at, Vector3.UnitY)
                * Matrix4x4.CreatePerspectiveFieldOfView(0.34f, 1f, 0.05f, 50f);

        _skinShader.Use();
        _skinShader.SetMatrix4("uViewProj", pvp);
        _skinShader.SetMatrix4("uModel", _player.CurrentTransform);
        _skinShader.SetInt("uAlbedo", 0);
        _skinShader.SetInt("uFlipV", 0);
        // Fixed studio key so the portrait reads the same in a crypt as in a
        // meadow; stamp invalidated below so the world re-uploads its own.
        _skinShader.SetInt("uDirCount", 1);
        _skinShader.SetFloat("uAmbient", 0.52f);
        Span<Vector3> pdir = stackalloc Vector3[1];
        pdir[0] = Vector3.Normalize(at - eye);
        Span<Vector3> pcol = stackalloc Vector3[1];
        pcol[0] = new Vector3(0.85f, 0.82f, 0.75f);
        _skinShader.SetVec3Array("uDirDir", pdir);
        _skinShader.SetVec3Array("uDirColor", pcol);
        _skinShader.SetInt("uPointCount", 0);
        _skinShader.SetInt("uUseBakedLight", 0);
        // SC-PORTRAIT-GAMMA — the portrait renders BEFORE the world skin
        // pass on a fresh boot, so the shared shader still holds the GL
        // default uGamma=0 → pow(lit, 10) crushed the hero to a grey
        // silhouette until some later re-render (equipping armor) happened
        // to run after the world had stamped its gamma. Same self-contained
        // uniform set HeroPreviewRenderer ships (397bc65): gamma is the
        // load-bearing one; fog and subset-tint guard against stale world
        // state bleeding into the studio shot.
        _skinShader.SetFloat("uGamma", _gammaLevel > 0.05f ? _gammaLevel : 1.0f);
        _skinShader.SetInt("uFogOn", 0);
        _skinShader.SetInt("uSubsetTintActive", 0);

        if (_skinScratch.Length < bones)
            _skinScratch = new Matrix4x4[Math.Max(bones, 64)];
        if (clip is not null)
            AnimationRuntime.ComputeSkinMatrices(mesh, clip, t, _skinScratch);
        else
            for (int i = 0; i < bones; i++) _skinScratch[i] = Matrix4x4.Identity;
        _skinShader.SetMatrix4Array("uBones[0]", _skinScratch.AsSpan(0, bones));

        var subsets = mesh.Subsets;
        if (subsets.Length == 0)
        {
            BindActorSlot(_player.Actor, 0);
            _player.GlMesh.Draw();
        }
        else
        {
            int lastSlot = -1;
            foreach (var sub in subsets)
            {
                if (sub.TextureIndex != lastSlot)
                {
                    BindActorSlot(_player.Actor, sub.TextureIndex);
                    lastSlot = sub.TextureIndex;
                }
                _player.GlMesh.DrawSubset(sub.FirstTriangle, sub.TriangleCount);
            }
        }

        // Worn equipment layers (helmet/armor) share the biped skeleton, so
        // the portrait shows gear exactly like retail's live portrait does.
        foreach (var layer in _equippedLayers)
        {
            int lb = layer.Asp.BoneCount;
            if (lb == 0 || lb > SkinnedMesh.MaxBones) continue;
            if (_skinScratch.Length < lb) _skinScratch = new Matrix4x4[Math.Max(lb, 64)];
            if (clip is not null)
                AnimationRuntime.ComputeSkinMatrices(layer.Asp, clip, t, _skinScratch);
            else
                for (int i = 0; i < lb; i++) _skinScratch[i] = Matrix4x4.Identity;
            _skinShader.SetMatrix4Array("uBones[0]", _skinScratch.AsSpan(0, lb));
            if (layer.Texture is not null)
            {
                layer.Texture.Bind(TextureUnit.Texture0);
                _skinShader.SetInt("uHasTexture", 1);
            }
            else _skinShader.SetInt("uHasTexture", 0);
            var lsub = layer.Asp.Subsets;
            if (lsub.Length == 0) layer.Mesh.Draw();
            else foreach (var sub in lsub) layer.Mesh.DrawSubset(sub.FirstTriangle, sub.TriangleCount);
        }

        // Read back, flip rows (GL reads bottom-up), rebuild the HUD texture.
        var rgba = new byte[PortraitPx * PortraitPx * 4];
        fixed (byte* p = rgba)
            _gl.ReadPixels(0, 0, PortraitPx, PortraitPx, GLEnum.Rgba, GLEnum.UnsignedByte, p);

        // Brightness guard — the general defense against every way a render
        // can silently fail (bad camera, culled geometry, missing textures):
        // a shot darker than the bare backdrop is REJECTED, the painted
        // template icon keeps showing, and the settle-retry gets another go.
        long lumSum = 0; int lumN = 0;
        for (int i = 0; i < rgba.Length; i += 4 * 13)
        { lumSum += rgba[i] + rgba[i + 1] + rgba[i + 2]; lumN += 3; }
        float avgLum = lumN > 0 ? lumSum / (float)lumN / 255f : 0f;
        if (avgLum > 0.075f)
        {
            // NO row flip: ReadPixels returns rows bottom-up, which is exactly
            // the DS1 .raw convention every icon texture already uses — the
            // HUD's portrait draw samples that orientation correctly. The
            // earlier pre-flip double-flipped and showed the head upside down.
            _playerPortraitLive?.Dispose();
            _playerPortraitLive = new GlTexture(_gl, rgba, PortraitPx, PortraitPx, nearestFilter: false);
            Console.WriteLine($"[portrait] live portrait rendered (avg luma {avgLum:F2})");
        }
        else
        {
            Console.WriteLine($"[portrait] render came back dark (avg luma {avgLum:F2}) — keeping fallback icon");
        }
        // One settle re-render shortly after each trigger so textures that
        // loaded a frame late land in the final shot.
        if (_portraitChainLeft > 0)
        {
            _portraitChainLeft--;
            _portraitRefreshAt = _playSeconds + 2.0;
        }

        // Restore world rendering state.
        _gl.Enable(GLEnum.CullFace);
        _gl.BindFramebuffer(GLEnum.Framebuffer, 0);
        var fbsz = _window.FramebufferSize;
        _gl.Viewport(0, 0, (uint)fbsz.X, (uint)fbsz.Y);
        _skinLightingStamp = ulong.MaxValue;   // force lighting re-upload
    }

    /// <summary>SC-MEGAMAP-CHROME — the authored mmap parchment border
    /// (b_gui_ig_mnu_mmap_* family): corners pinned, edges stretched.
    /// Missing pieces skip quietly so a partial art set still frames.</summary>
    private void DrawMegaMapChrome(int vw, int vh)
    {
        if (_iconRenderer is null) return;
        var tl = TryGetGuiTexture("b_gui_ig_mnu_mmap_tl");
        var tr = TryGetGuiTexture("b_gui_ig_mnu_mmap_tr");
        var ll = TryGetGuiTexture("b_gui_ig_mnu_mmap_ll");
        var lr = TryGetGuiTexture("b_gui_ig_mnu_mmap_lr");
        var tt = TryGetGuiTexture("b_gui_ig_mnu_mmap_t");
        var bb = TryGetGuiTexture("b_gui_ig_mnu_mmap_bot");
        var le = TryGetGuiTexture("b_gui_ig_mnu_mmap_l");
        var re = TryGetGuiTexture("b_gui_ig_mnu_mmap_r");
        float s = Hud.HudScale.Hud(vh);
        int corner = (int)(48 * s), edge = (int)(24 * s);
        if (tt is not null) _iconRenderer.DrawIcon(vw, vh, tt, corner, 0, vw - 2 * corner, edge, Vector4.One);
        if (bb is not null) _iconRenderer.DrawIcon(vw, vh, bb, corner, vh - edge, vw - 2 * corner, edge, Vector4.One);
        if (le is not null) _iconRenderer.DrawIcon(vw, vh, le, 0, corner, edge, vh - 2 * corner, Vector4.One);
        if (re is not null) _iconRenderer.DrawIcon(vw, vh, re, vw - edge, corner, edge, vh - 2 * corner, Vector4.One);
        if (tl is not null) _iconRenderer.DrawIcon(vw, vh, tl, 0, 0, corner, corner, Vector4.One);
        if (tr is not null) _iconRenderer.DrawIcon(vw, vh, tr, vw - corner, 0, corner, corner, Vector4.One);
        if (ll is not null) _iconRenderer.DrawIcon(vw, vh, ll, 0, vh - corner, corner, corner, Vector4.One);
        if (lr is not null) _iconRenderer.DrawIcon(vw, vh, lr, vw - corner, vh - corner, corner, corner, Vector4.One);
    }

    /// <summary>SC-QUEST-UI-B — compact HUD quest tracker: the most recently
    /// activated quest's screen name + objective text + progress fraction,
    /// pinned to the left edge. Crediting used to be invisible without the
    /// console or opening the journal; this is the always-on read. Prefers
    /// catalog-defined entries (they carry objective text and goals).</summary>
    private void DrawQuestTracker(int viewportW, int viewportH)
    {
        if (_progression is null || _textRenderer is null) return;
        SiegeFX.Core.Actors.QuestEntry? pick = null;
        foreach (var e in _progression.Journal.Active)
            if (e.Definition is not null || pick is null) pick = e;
        if (pick is null) return;

        var d = pick.Definition;
        string title = d?.ScreenName is { Length: > 0 } sn ? sn : pick.Key;
        string line2 = "";
        if (d is not null)
        {
            string counter =
                d.KillCountGoal > 0   ? $" ({pick.KillProgress}/{d.KillCountGoal})" :
                d.TalkCountGoal > 1   ? $" ({pick.TalkProgress}/{d.TalkCountGoal})" :
                d.PickupCountGoal > 1 ? $" ({pick.PickupProgress}/{d.PickupCountGoal})" : "";
            // Shorten the objective, not the counter: a long objective used
            // to push the counter off the end of the line.
            string objective = d.ObjectiveText;
            int room = 72 - counter.Length;
            if (objective.Length > room) objective = objective[..(room - 3)] + "...";
            line2 = objective + counter;
        }

        int x = 12;
        int y = (int)(viewportH * 0.30f);
        var gold = new Vector4(1.00f, 0.85f, 0.40f, 0.95f);
        var ink  = new Vector4(0.92f, 0.92f, 0.88f, 0.85f);
        _textRenderer.DrawString(viewportW, viewportH, title, x, y, gold);
        if (line2.Length > 0)
            _textRenderer.DrawString(viewportW, viewportH, line2, x, y + 16, ink);
    }

    private void DrawSubtitles(int viewportW, int viewportH)
    {
        if (_subtitleNodes is null || _subtitleLines.Length == 0 || _textRenderer is null) return;
        int start = _subtitlePage * SubtitleLinesPerPage;
        int count = Math.Min(SubtitleLinesPerPage, _subtitleLines.Length - start);
        if (count <= 0) return;

        // DS1 renders the NIS narration + quest text down inside the bottom letterbox
        // bar (light ink on the black reads much more easily). Bump the bitmap font one
        // integer size and vertically center the block within the bar.
        const int scale = 2;
        int lineH = _textRenderer.LineHeight * scale + 4;
        int barH = (int)(viewportH * 0.125f * _nisLetterbox);
        if (barH <= 0) return;
        int barTop = viewportH - barH;
        int blockH = count * lineH;
        int y0 = barTop + Math.Max(2, (barH - blockH) / 2);
        var ink = new Vector4(0.96f, 0.94f, 0.85f, 1f);
        for (int i = 0; i < count; i++)
        {
            var line = _subtitleLines[start + i];
            int w = _textRenderer.MeasureWidth(line, scale);
            int x = Math.Max(8, viewportW / 2 - w / 2);
            _textRenderer.DrawString(viewportW, viewportH, line, x, y0 + i * lineH, ink, scale);
        }
    }

    private void DrawCompass(int viewportW, int viewportH)
    {
        if (_iconRenderer is null) return;
        EnsureCompassTextures();
        if (_compassCover is null) return;
        float scale = Hud.HudScale.Hud(viewportH); // ALPHA-2V — shared HUD baseline + UI-scale knob
        var (rx, ry, size) = CompassRect(viewportW, viewportH);
        int cx = rx + size / 2;
        int cy = ry + size / 2;
        var tint = new Vector4(1f, 1f, 1f, 1f);

        // Collapsed: the dial folds down to the small compass-star show button.
        if (_compassHidden)
        {
            if (_compassShowBtn is not null)
            {
                var (sx, sy, sw, sh) = CompassShowRect(cx, cy, scale);
                _iconRenderer.DrawIcon(viewportW, viewportH, _compassShowBtn, sx, sy, sw, sh, tint);
            }
            return;
        }

        if (_compassFace is not null)
            _iconRenderer.DrawIcon(viewportW, viewportH, _compassFace,
                cx - size / 2, cy - size / 2, size, size, tint);
        // Static bronze arrowhead docked at the dial's NE corner (hide button).
        // Drawn UNDER the cover so the ornate ring overlaps its base — it reads
        // as tucked under a fold of the compass at the top-right, per the gas.
        if (_compassHideBtn is not null)
        {
            var (hx, hy, hw, hh) = CompassHideRect(cx, cy, scale);
            _iconRenderer.DrawIcon(viewportW, viewportH, _compassHideBtn, hx, hy, hw, hh, tint);
        }
        _iconRenderer.DrawIcon(viewportW, viewportH, _compassCover,
            cx - size / 2, cy - size / 2, size, size, tint);
        float camYaw = _camera.Yaw;
        void Letter(GlTexture? tex, float worldYaw)
        {
            if (tex is null) return;
            float a = worldYaw - camYaw;
            float dist = 28f * scale;
            int lw = (int)(14 * scale), lh = (int)(14 * scale);
            int lx = cx + (int)(MathF.Sin(a) * dist) - lw / 2;
            int ly = cy - (int)(MathF.Cos(a) * dist) - lh / 2;
            _iconRenderer.DrawIcon(viewportW, viewportH, tex, lx, ly, lw, lh, tint);
        }
        Letter(_compassN, 0f);             // world -Z
        Letter(_compassE, MathF.PI / 2f);  // world +X
        Letter(_compassS, MathF.PI);       // world +Z
        Letter(_compassW, -MathF.PI / 2f); // world -X
    }

    /// <summary>SC-WEATHER-E — full-screen lightning wash. Intensity is the
    /// WeatherSystem strike envelope; capped alpha keeps the HUD legible
    /// through the flash while the fog-color boost (ApplyLightingUniforms)
    /// does the heavy lifting of lighting the world itself.</summary>
    private void DrawWeatherFlash(int viewportW, int viewportH)
    {
        if (_weather.FlashIntensity <= 0f || _barRenderer is null) return;
        var wash = new Vector4(1f, 1f, 1f, _weather.FlashIntensity * 0.40f);
        _barRenderer.DrawRect(viewportW, viewportH, 0, 0, viewportW, viewportH, wash);
    }

    private void DrawNisLetterbox(int viewportW, int viewportH)
    {
        if (_nisLetterbox <= 0f || _barRenderer is null) return;
        // widescreen_height default 0.75 → 12.5% black bars top and bottom.
        int barH = (int)(viewportH * 0.125f * _nisLetterbox);
        if (barH <= 0) return;
        var black = new Vector4(0f, 0f, 0f, 1f);
        _barRenderer.DrawRect(viewportW, viewportH, 0, 0, viewportW, barH, black);
        _barRenderer.DrawRect(viewportW, viewportH, 0, viewportH - barH, viewportW, barH, black);
    }

    private void DrawVictoryOverlay(int vw, int vh)
    {
        if (!_victoryActive || _barRenderer is null || _textRenderer is null) return;
        _victoryTimer += (float)_frameDtSeconds;
        float dimT = Math.Clamp(_victoryTimer / 1.5f, 0f, 1f);
        float alpha = 0.45f * dimT;
        var dimTex = TryGetGuiTexture("b_gui_cmn_textbox_bg");
        if (dimTex is not null && _iconRenderer is not null)
            _iconRenderer.DrawIcon(vw, vh, dimTex, 0, 0, vw, vh, new Vector4(1f, 1f, 1f, alpha));
        else
            _barRenderer.DrawRect(vw, vh, 0, 0, vw, vh, new Vector4(0.06f, 0.05f, 0.04f, alpha));
        if (dimT < 1f) { _victoryOkRect = _victoryXRect = default; return; }

        float s = Hud.HudScale.Modal(vw, vh);
        int dx = (vw - (int)(640 * s)) / 2;
        int dy = (vh - (int)(480 * s)) / 2;
        (int x, int y, int w, int h) R(int x0, int y0, int x1, int y1) =>
            (dx + (int)(x0 * s), dy + (int)(y0 * s), (int)((x1 - x0) * s), (int)((y1 - y0) * s));
        int fs = Math.Max(1, (int)MathF.Round(s));

        var main = R(122, 131, 507, 355);
        var inner = R(143, 145, 479, 309);
        if (_iconRenderer is not null)
        {
            Hud.NinePatch.DrawCpbox(_iconRenderer, GetCommonTexture, vw, vh, main.x, main.y, main.w, main.h, Vector4.One);
            Hud.NinePatch.DrawCpbox(_iconRenderer, GetCommonTexture, vw, vh, inner.x, inner.y, inner.w, inner.h, Vector4.One);
        }
        else
            _barRenderer.DrawRect(vw, vh, main.x, main.y, main.w, main.h, new Vector4(0.05f, 0.05f, 0.05f, 0.92f));

        // Authored text, wrapped and vertically centered (center_height).
        var tb = R(146, 148, 475, 305);
        var lines = WrapText(EndGameText, 46).ToList();
        int lineH = _textRenderer.LineHeight * fs + 2;
        int ty = tb.y + (tb.h - lines.Count * lineH) / 2;
        foreach (var line in lines)
        {
            int lw = _textRenderer.MeasureWidth(line, fs);
            _textRenderer.DrawString(vw, vh, line, tb.x + (tb.w - lw) / 2, ty, Vector4.One, fs);
            ty += lineH;
        }

        // OK (button_4 chrome) + corner X — both notify(eg_close).
        var ok = R(262, 319, 362, 335);
        if (!Hud.ButtonChrome.Draw(_iconRenderer, TryGetGuiTexture, vw, vh, ok.x, ok.y, ok.w, ok.h, "button4", Hud.ButtonChrome.State.Up))
        {
            _barRenderer.DrawRect(vw, vh, ok.x, ok.y, ok.w, ok.h, new Vector4(0.14f, 0.13f, 0.11f, 1f));
            _barRenderer.DrawBorder(vw, vh, ok.x, ok.y, ok.w, ok.h, new Vector4(0.55f, 0.52f, 0.45f, 1f));
        }
        int okw = _textRenderer.MeasureWidth("OK", fs);
        _textRenderer.DrawString(vw, vh, "OK",
            ok.x + (ok.w - okw) / 2, ok.y + (ok.h - _textRenderer.LineHeight * fs) / 2, Vector4.One, fs);
        var xr = R(486, 134, 502, 150);
        var xTex = TryGetGuiTexture("b_gui_cmn_button_x_up");
        if (xTex is not null && _iconRenderer is not null)
            _iconRenderer.DrawIcon(vw, vh, xTex, xr.x, xr.y, xr.w, xr.h, Vector4.One);
        _victoryOkRect = ok;
        _victoryXRect = xr;
    }

    /// <summary>SC-DEFEAT — the authored presentation. 640×480 gas rects
    /// scale through the same modal mapping the other in-game dialogs use.</summary>
    private void DrawDefeatOverlay(int vw, int vh)
    {
        if (_defeatPhase == DefeatPhase.None || _barRenderer is null || _textRenderer is null) return;

        float s = Hud.HudScale.Modal(vw, vh);
        int dx = (vw - (int)(640 * s)) / 2;
        int dy = (vh - (int)(480 * s)) / 2;
        (int x, int y, int w, int h) R(int x0, int y0, int x1, int y1) =>
            (dx + (int)(x0 * s), dy + (int)(y0 * s), (int)((x1 - x0) * s), (int)((y1 - y0) * s));

        // defeat_menu background: full-screen textbox_bg at authored alpha
        // 0.45, fading in over the Dimming beat.
        float dimT = _defeatPhase == DefeatPhase.Dimming ? Math.Clamp(_defeatTimer / 1.5f, 0f, 1f) : 1f;
        float alpha = 0.45f * dimT;
        var dimTex = TryGetGuiTexture("b_gui_cmn_textbox_bg");
        if (dimTex is not null && _iconRenderer is not null)
            _iconRenderer.DrawIcon(vw, vh, dimTex, 0, 0, vw, vh, new Vector4(1f, 1f, 1f, alpha));
        else
            _barRenderer.DrawRect(vw, vh, 0, 0, vw, vh, new Vector4(0.06f, 0.05f, 0.04f, alpha));

        // "Defeat!" — 20p copperplate, rect (0,100,640,140) centered.
        if (dimT > 0.4f)
        {
            var tr = R(0, 100, 640, 140);
            int ts = Math.Max(1, (int)MathF.Round(s));
            int tw = _textRenderer.MeasureWidth("20p", "Defeat!") * ts;
            int th = _textRenderer.LineHeightOf("20p") * ts;
            _textRenderer.DrawString("20p", vw, vh, "Defeat!",
                tr.x + (tr.w - tw) / 2, tr.y + (tr.h - th) / 2,
                new Vector4(1f, 1f, 1f, Math.Clamp((dimT - 0.4f) / 0.6f, 0f, 1f)), ts);
        }

        _defeatButtons.Clear();
        if (!DefeatDialogActive) return;

        int fs = Math.Max(1, (int)MathF.Round(s));
        // dialog_main_bg + dialog_text_bg — both cpbox.
        var main = R(130, 170, 510, 310);
        var inner = R(150, 190, 490, 258);
        if (_iconRenderer is not null)
        {
            Hud.NinePatch.DrawCpbox(_iconRenderer, GetCommonTexture, vw, vh, main.x, main.y, main.w, main.h, Vector4.One);
            Hud.NinePatch.DrawCpbox(_iconRenderer, GetCommonTexture, vw, vh, inner.x, inner.y, inner.w, inner.h, Vector4.One);
        }
        else
        {
            _barRenderer.DrawRect(vw, vh, main.x, main.y, main.w, main.h, new Vector4(0.05f, 0.05f, 0.05f, 0.92f));
        }

        // text_defeat_game — runtime string, centered in (155,215,484,232).
        const string question = "Do you want to load a saved game?";
        var qr = R(155, 215, 484, 232);
        int qw = _textRenderer.MeasureWidth(question, fs);
        _textRenderer.DrawString(vw, vh, question,
            qr.x + (qr.w - qw) / 2, qr.y + (qr.h - _textRenderer.LineHeight * fs) / 2,
            Vector4.One, fs);

        // YES / NO / HELP — button_4 chrome with centered 12p labels.
        void DefeatButton(string id, string label, int x0, int y0, int x1, int y1)
        {
            var r = R(x0, y0, x1, y1);
            var st = _defeatPressed == id ? Hud.ButtonChrome.State.Down
                   : _defeatHover == id ? Hud.ButtonChrome.State.Hover
                   : Hud.ButtonChrome.State.Up;
            if (!Hud.ButtonChrome.Draw(_iconRenderer, TryGetGuiTexture, vw, vh, r.x, r.y, r.w, r.h, "button4", st))
            {
                _barRenderer.DrawRect(vw, vh, r.x, r.y, r.w, r.h, new Vector4(0.14f, 0.13f, 0.11f, 1f));
                _barRenderer.DrawBorder(vw, vh, r.x, r.y, r.w, r.h, new Vector4(0.55f, 0.52f, 0.45f, 1f));
            }
            int lw = _textRenderer.MeasureWidth(label, fs);
            _textRenderer.DrawString(vw, vh, label,
                r.x + (r.w - lw) / 2, r.y + (r.h - _textRenderer.LineHeight * fs) / 2,
                Vector4.One, fs);
            _defeatButtons.Add((id, r.x, r.y, r.w, r.h));
        }
        DefeatButton("yes",  "Yes",  160, 276, 250, 292);
        DefeatButton("no",   "No",   263, 276, 353, 292);
        DefeatButton("help", "Help", 386, 276, 476, 292);
    }

    /// <summary>World-space pass: selection rings under every selected party
    /// member + the fading destination tetra. Runs right after the blob
    /// shadows (same depth conventions).</summary>
    private void DrawSelectionWorldFx(Matrix4x4 vp)
    {
        if (_gl is null || _player is null || _cameraMode != CameraMode.Chase) return;
        _selectionFx ??= new SelectionFxRenderer(_gl);
        _selectionFx.Begin();

        var ringColor = new Vector4(0.05f, 0.78f, 0.10f, 0.92f);
        foreach (var idx in _selectedPartyIdx)
        {
            var m = idx == 0 ? _player
                : idx == SummonSelIdx ? PlayerSummon
                : _party.FirstOrDefault(p => p.PartyIndex == idx);
            if (m is null || m.IsDead) continue;
            _selectionFx.AddRing(m.CurrentTransform.Translation, 0.62f, ringColor);
        }
        if (_moveMarkerPos is { } mp)
        {
            float fade = 1f - Math.Clamp(_moveMarkerAge / MoveMarkerLife, 0f, 1f);
            _selectionFx.AddInvertedTetra(mp, 0.30f,
                new Vector4(0.05f, 0.72f, 0.10f, 0.75f * fade));
        }
        // Phase 22 — hollow blue hover triangle on gold/spell piles. DS1 draws
        // it as a darker-blue OUTLINE with an empty (transparent) middle;
        // AddFlatTriangle now emits the frame, this sets the darker tint.
        if (_hoverPile is { } hoverPile && _hoverPileIsGoldOrSpell)
            _selectionFx.AddFlatTriangle(hoverPile.Position, 0.45f,
                new Vector4(0.06f, 0.16f, 0.62f, 0.85f));
        _selectionFx.Draw(vp);
        // Phase 21 — combat blood splats ride the same world pass.
        _bloodSplats ??= _gl is not null ? new BloodSplatRenderer(_gl) : null;
        _bloodSplats?.Draw(vp, _bloodTextures);
    }

    /// <summary>HUD pass: the thin green drag marquee + the bottom-center
    /// hover status line ("Woot: Health 49/49, Mana 30/30").</summary>
    private void DrawMouseHudFx(int vw, int vh)
    {
        if (_barRenderer is null || _textRenderer is null) return;
        // SC-CMD-KEYS — authored Game Timer (semicolon): the character's
        // elapsed play clock, top-center readout while toggled on.
        if (_gameTimerVisible)
        {
            var el = TimeSpan.FromSeconds(_playSeconds);
            string clock = $"TIME {(int)el.TotalHours}:{el.Minutes:00}:{el.Seconds:00}";
            float ts = Hud.HudScale.Hud(vh);
            int px2 = Math.Max(1, (int)MathF.Round(ts));
            _textRenderer.DrawString(vw, vh, clock,
                vw / 2 - (int)(34 * ts), (int)(6 * ts),
                new Vector4(0.95f, 0.9f, 0.6f, 0.95f), px2);
        }
        if (_marqueeActive && _lmbWorldDownPos is { } dp)
        {
            int x0 = (int)MathF.Min(dp.X, _marqueeEnd.X), x1 = (int)MathF.Max(dp.X, _marqueeEnd.X);
            int y0 = (int)MathF.Min(dp.Y, _marqueeEnd.Y), y1 = (int)MathF.Max(dp.Y, _marqueeEnd.Y);
            _barRenderer.DrawBorder(vw, vh, x0, y0, x1 - x0, y1 - y0,
                new Vector4(0.10f, 0.95f, 0.15f, 0.95f));
        }
        // SC-OPTIONS-GAME — "Show Rollover Help" gates the hover readout.
        if (_showTooltips && _hoverActor is { } ha)
        {
            string name = ResolveMemberName(ha);
            var combat = ha.Actor.Combat;
            var stats = ha.Actor.Stats;
            bool friendly = ha.IsPlayer || ha.IsPartyMember;
            // Phase 22 — corpses read in red ("Dead Krug Scavenger: Health
            // 0/8"), matching the original's dead-hover line.
            bool dead = ha.IsDead || combat.IsDead;
            string line = dead
                ? $"Dead {name}: Health 0/{stats.MaxLife:F0}"
                : friendly && stats.MaxMana > 0
                    ? $"{name}: Health {combat.CurrentLife:F0}/{stats.MaxLife:F0}, Mana {combat.CurrentMana:F0}/{stats.MaxMana:F0}"
                    : $"{name}: Health {combat.CurrentLife:F0}/{stats.MaxLife:F0}";
            int fs = Math.Max(1, (int)MathF.Round(Hud.HudScale.Modal(vw, vh)));
            int lw = _textRenderer.MeasureWidth(line, fs);
            _textRenderer.DrawString(vw, vh, line, (vw - lw) / 2,
                vh - (int)(20 * fs) - _textRenderer.LineHeight * fs / 2,
                dead ? new Vector4(0.88f, 0.15f, 0.12f, 1f)
                     : new Vector4(0.42f, 0.95f, 0.38f, 1f), fs);
        }
        // Phase 22 — hovered ground item: two-line bottom readout ("Fireshot"
        // / "Left-click to pick up item") per the blue-triangle reference.
        else if (_showTooltips && _hoverPile is { } hp)
        {
            int fs = Math.Max(1, (int)MathF.Round(Hud.HudScale.Modal(vw, vh)));
            var ink = new Vector4(0.92f, 0.88f, 0.76f, 1f);
            string l1 = PileLabel(hp);
            string l2 = "Left-click to pick up item";
            int w1 = _textRenderer.MeasureWidth(l1, fs);
            int w2 = _textRenderer.MeasureWidth(l2, fs);
            int baseY = vh - (int)(20 * fs) - _textRenderer.LineHeight * fs;
            _textRenderer.DrawString(vw, vh, l1, (vw - w1) / 2, baseY, ink, fs);
            _textRenderer.DrawString(vw, vh, l2, (vw - w2) / 2,
                baseY + _textRenderer.LineHeight * fs, ink, fs);
        }
    }

    private void DrawDevConsole(int vw, int vh)
    {
        if (!_devConsoleOpen || _barRenderer is null || _textRenderer is null) return;
        _devButtons.Clear();

        const int pad = 8, rowH = 22, btnH = 20;
        int w = 280;
        int x = 12, y = 72;
        int visRegions = Math.Min(12, Math.Max(0, (_devRegionList?.Count ?? 0)));
        int h = pad + 16 + (rowH * 5) + 30 + 22 + (visRegions * 18) + pad + 20;
        _devPanelRect = (x, y, w, h);

        _barRenderer.DrawRect(vw, vh, x, y, w, h, new Vector4(0.05f, 0.06f, 0.08f, 0.92f));
        _barRenderer.DrawBorder(vw, vh, x, y, w, h, new Vector4(0.35f, 0.75f, 0.45f, 1f));

        var ink   = new Vector4(0.80f, 0.95f, 0.82f, 1f);
        var dim   = new Vector4(0.55f, 0.65f, 0.58f, 1f);
        var hot   = new Vector4(1f, 0.85f, 0.35f, 1f);
        int cy = y + pad;
        _textRenderer.DrawString(vw, vh, "DEV CONSOLE  (~)", x + pad, cy, hot, 1);
        cy += 18;

        void Button(string id, string label, int bx, int bw, bool lit = false)
        {
            _barRenderer.DrawRect(vw, vh, bx, cy, bw, btnH, new Vector4(0.12f, 0.16f, 0.14f, 1f));
            _barRenderer.DrawBorder(vw, vh, bx, cy, bw, btnH, lit ? hot : new Vector4(0.30f, 0.45f, 0.35f, 1f));
            _textRenderer.DrawString(vw, vh, label, bx + 6, cy + 4, lit ? hot : ink, 1);
            _devButtons.Add((id, bx, cy, bw, btnH));
        }

        Button("god",  _devGodMode ? "GOD MODE: ON" : "GOD MODE: off", x + pad, 130, _devGodMode);
        Button("heal", "HEAL FULL", x + pad + 138, 126);
        cy += rowH;
        Button("gold1",  "+1,000 GOLD", x + pad, 130);
        Button("gold10", "+10,000 GOLD", x + pad + 138, 126);
        cy += rowH;
        Button("level", "LEVEL UP", x + pad, 130);
        Button("kill",  "KILL NEARBY", x + pad + 138, 126);
        cy += rowH;
        Button("devcam", _devFreeCamera ? "DEV CAMERA: ON" : "DEV CAMERA: off", x + pad, 264, _devFreeCamera);
        cy += rowH;

        // Item spawn: text field + button.
        _barRenderer.DrawRect(vw, vh, x + pad, cy, 190, btnH, new Vector4(0.02f, 0.03f, 0.04f, 1f));
        _barRenderer.DrawBorder(vw, vh, x + pad, cy, 190, btnH,
            _devItemFocus ? hot : new Vector4(0.30f, 0.45f, 0.35f, 1f));
        string shown = _devItemText.Length == 0 && !_devItemFocus ? "item template / #spec" : _devItemText;
        _textRenderer.DrawString(vw, vh, shown + (_devItemFocus ? "_" : ""),
            x + pad + 5, cy + 4, _devItemText.Length == 0 ? dim : ink, 1);
        _devButtons.Add(("itemfield", x + pad, cy, 190, btnH));
        Button("spawn", "SPAWN", x + pad + 198, 66);
        cy += rowH + 8;

        _textRenderer.DrawString(vw, vh, $"TELEPORT — wheel scrolls ({_devRegionList?.Count ?? 0})",
            x + pad, cy, dim, 1);
        cy += 18;
        if (_devRegionList is { Count: > 0 })
        {
            _devRegionScroll = Math.Clamp(_devRegionScroll, 0, Math.Max(0, _devRegionList.Count - visRegions));
            for (int i = 0; i < visRegions; i++)
            {
                int ri = _devRegionScroll + i;
                if (ri >= _devRegionList.Count) break;
                var path = _devRegionList[ri];
                var name = path[(path.LastIndexOf('/') + 1)..];
                bool current = string.Equals(path.TrimEnd('/'), _currentPlayerRegion?.TrimEnd('/'),
                                             StringComparison.OrdinalIgnoreCase);
                _textRenderer.DrawString(vw, vh, (current ? "> " : "  ") + name,
                    x + pad + 4, cy + 2, current ? hot : ink, 1);
                _devButtons.Add(($"rgn:{path}", x + pad, cy, w - pad * 2, 17));
                cy += 18;
            }
        }
    }

    /// <summary>Phase 24-MAINMENU step 1+2 — render the splash → main menu
    /// frame for boot mode. Drives the FrontendScene state machine via
    /// Tick(dt) and renders the active splash's three panels with the
    /// state-driven alpha. Subsequent batches replace the FadeOut beat
    /// with the gpg_intro.bik playback + logo drop, and the MainMenu
    /// state with the real 7-button frontend chrome.</summary>
    private void DrawBootScene(float dt, int viewportW, int viewportH)
    {
        if (_frontendScene is null || _barRenderer is null || _iconRenderer is null
            || _textRenderer is null || _playResolver is null) return;
        // Phase 24-MAINMENU step 1+2-FOLD — Tick moved to OnUpdate so the
        // splash advances even when Render is paused (e.g. window minimized).
        // Solid black backdrop — the splash strip is centered on a 640×480
        // authored canvas and we letterbox the rest.
        _textRenderer.BeginPass();
        _barRenderer.DrawRect(viewportW, viewportH, 0, 0, viewportW, viewportH,
            new Vector4(0f, 0f, 0f, 1f));

        var prefix = _frontendScene.IntroTexturePrefix;
        var alpha  = _frontendScene.IntroAlpha;
        if (prefix is not null && alpha > 0f)
        {
            // Authored canvas is 640×480; uniform-scale by the smaller of
            // the two viewport-to-authored ratios so the splash never
            // crops, then center on whatever space remains.
            float s = MathF.Min(viewportH / 480f, viewportW / 640f);
            int dx = (viewportW - (int)MathF.Round(640 * s)) / 2;
            int dy = (viewportH - (int)MathF.Round(480 * s)) / 2;
            // intro_microsoft.gas / intro_gaspowered.gas authored rects:
            //   panel_1 = 0,0,256,256 → b_gui_nis_<set>_01
            //   panel_2 = 256,0,512,256 → b_gui_nis_<set>_02
            //   panel_3 = 512,0,640,256 → b_gui_nis_<set>_03 (memory says
            //              .raw is 256×128; rect is 128×256 — host stretches
            //              non-uniformly. Visual verification pending in
            //              batch 1 self-test; flag in
            //              project_siegefx_frontend_assets.md if wrong.)
            DrawSplashPanel(prefix + "01", 0,   0, 256, 256, s, dx, dy, alpha, viewportW, viewportH);
            DrawSplashPanel(prefix + "02", 256, 0, 256, 256, s, dx, dy, alpha, viewportW, viewportH);
            DrawSplashPanel(prefix + "03", 512, 0, 128, 256, s, dx, dy, alpha, viewportW, viewportH);
        }
        // Phase 24-MAINMENU step 1-6 — once the splash sequence drains
        // past the Bink-stub fade, hand drawing to the existing
        // FrontendScene composer for IntroLogoDrop (backdrop + sides
        // + logo.asp with logo-enter.prs) AND for MainMenu state. The
        // chrome is correct DS1 art but currently in the wrong pose
        // (cd-state placeholder until splinter SC-MAINMENU-CHROME-PROPER
        // wires the real main-menu-pose PRS clips + subset masks); it
        // still reads as "DS1 chrome" rather than a black void, which
        // the user prefers per the polish triage even before the pose
        // pass lands.
        else if (_frontendScene.State == Hud.FrontendScene.ScreenState.IntroLogoDrop
              || _frontendScene.State == Hud.FrontendScene.ScreenState.IntroLogoHold
              || _frontendScene.State == Hud.FrontendScene.ScreenState.IntroLogoExit
              || _frontendScene.State == Hud.FrontendScene.ScreenState.IntroMenuFlyIn
              || _frontendScene.State == Hud.FrontendScene.ScreenState.MainMenu
              || _frontendScene.State == Hud.FrontendScene.ScreenState.MainMenuToSp
              || _frontendScene.State == Hud.FrontendScene.ScreenState.SinglePlayer
              || _frontendScene.State == Hud.FrontendScene.ScreenState.SinglePlayerToMm
              || _frontendScene.State == Hud.FrontendScene.ScreenState.SinglePlayerToLg
              || _frontendScene.State == Hud.FrontendScene.ScreenState.LoadGame
              || _frontendScene.State == Hud.FrontendScene.ScreenState.LgToSinglePlayer
              || _frontendScene.State == Hud.FrontendScene.ScreenState.SinglePlayerToCd
              || _frontendScene.State == Hud.FrontendScene.ScreenState.CharacterSelect
              || _frontendScene.State == Hud.FrontendScene.ScreenState.CharacterSelectToSp
              || _frontendScene.State == Hud.FrontendScene.ScreenState.CharacterSelectToDifficulty
              || _frontendScene.State == Hud.FrontendScene.ScreenState.Difficulty
              || _frontendScene.State == Hud.FrontendScene.ScreenState.DifficultyToCharacterSelect
              || _frontendScene.State == Hud.FrontendScene.ScreenState.MainMenuToMp
              || _frontendScene.State == Hud.FrontendScene.ScreenState.Multiplayer
              || _frontendScene.State == Hud.FrontendScene.ScreenState.MpToMainMenu
              || _frontendScene.State == Hud.FrontendScene.ScreenState.MpInternet
              || _frontendScene.State == Hud.FrontendScene.ScreenState.MpNetwork
              || _frontendScene.State == Hud.FrontendScene.ScreenState.MpStaging)
        {
            // Phase 26-VIEWPORT — clip rendering to the chrome's letterbox
            // area so meshes that extend BEYOND backdrop's authored bounds
            // (pillars at ±2.17 vs backdrop's ±1.64; mainmenu/menubars at Y
            // up to 5.18 in bind pose) don't bleed into the side bars or
            // above/below the visible screen frame. DS1 was authored 4:3
            // with the assumption that the viewport crops the outer
            // fringes; at modern 16:9 / ultrawide / 4K resolutions we have
            // to enforce that crop explicitly.
            //
            // Letterbox aspect = backdrop's authored aspect (~1.32, very
            // close to 4:3). On widescreen the chrome fills vertical
            // height, the sides letterbox to black. On taller-than-4:3
            // the chrome fills horizontal width, top/bottom letterbox.
            const float chromeAspect = 1.32f;
            int boxW, boxH;
            float vpAspect = viewportW / (float)viewportH;
            if (vpAspect > chromeAspect)
            {
                boxH = viewportH;
                boxW = (int)(viewportH * chromeAspect);
            }
            else
            {
                boxW = viewportW;
                boxH = (int)(viewportW / chromeAspect);
            }
            int boxX = (viewportW - boxW) / 2;
            // glScissor uses framebuffer coords (origin BOTTOM-left); our
            // y values are top-left so we have to flip when computing scissor Y.
            int boxYTop = (viewportH - boxH) / 2;
            int boxYGlBottom = viewportH - boxYTop - boxH;
            _gl?.Enable(EnableCap.ScissorTest);
            _gl?.Scissor(boxX, boxYGlBottom, (uint)boxW, (uint)boxH);
            _frontendScene.Draw(viewportW, viewportH);
            if (_frontendScene.State == Hud.FrontendScene.ScreenState.MainMenu)
            {
                // MainMenuPanel owns hit-testing + the click→action pipeline
                // for all 7 buttons; it renders nothing visual now (Phase 26-
                // ARTMAP). The 5 menubars buttons render through menubars.asp
                // chrome inside FrontendScene.Draw above; the EXIT button
                // renders through backbutton.asp + art_mapping.gas overrides
                // immediately below.
                _mainMenu.Draw(_barRenderer, _textRenderer, _iconRenderer, viewportW, viewportH);
                // Phase 26-ARTMAP — render the EXIT button via the proper
                // DS1 asset chain. MainMenuPanel exposes the screen rect +
                // hover/press state; FrontendScene runs the asp draw with
                // art_mapping.gas's [button_exit] texture-swap recipe.
                // SC-MAIN-HOVER — DrawExitButton is the source of truth
                // for the EXIT button render (chrome's mainmenu draw
                // doesn't isolate this widget cleanly). Fire always so
                // mouseout state shows the engraved EXIT; hover/press
                // swap to exitback-up/-down + text-small-up/-down.
                if (_mainMenu.TryGetButtonStateAndRect(
                        Hud.MainMenuPanel.Action.Exit,
                        viewportW, viewportH,
                        out int ex, out int ey, out int ew, out int eh,
                        out bool eHover, out bool ePress))
                {
                    _frontendScene.DrawExitButton(viewportW, viewportH,
                        ex, ey, ew, eh, eHover, ePress);
                }
                // SC-MAIN-HOVER — per-widget chrome render for each main
                // menu button via DrawMenubarsButton + art_mapping.gas
                // recipe. Fires always (text labels live on text-menubars1
                // subsets that DrawMainMenuState's menubarsMask doesn't
                // include); hover/press swap to menubars-up/-down +
                // text-menubars1-up/-down. Same engraved-button pattern
                // SP submenu uses.
                (Hud.MainMenuPanel.Action act, string widget)[] mmButtons =
                {
                    (Hud.MainMenuPanel.Action.SinglePlayer, "button_single_player"),
                    (Hud.MainMenuPanel.Action.Multiplayer,  "button_multi_player"),
                    (Hud.MainMenuPanel.Action.Options,      "button_options"),
                    (Hud.MainMenuPanel.Action.Continue,     "button_continue"),
                    (Hud.MainMenuPanel.Action.About,        "button_about"),
                };
                foreach (var (act, widget) in mmButtons)
                {
                    if (_mainMenu.TryGetButtonStateAndRect(act, viewportW, viewportH,
                            out int mx, out int my, out int mw, out int mh,
                            out bool mHov, out bool mPr))
                    {
                        _frontendScene.DrawMenubarsButton(viewportW, viewportH,
                            mx, my, mw, mh, mHov, mPr, widget);
                    }
                }
                // Phase 27-SP-FLYOUT — hover overlay on every main menu
                // button so the user gets visual feedback under the cursor.
                // Subtle white tint (alpha .12) layered over the wood
                // chrome — reads as a soft glow without obscuring the
                // engraved label. Same overlay applies to EXIT.
                DrawMainMenuHoverOverlays(viewportW, viewportH);
                if (_aboutOpen)
                    DrawAboutOverlay(viewportW, viewportH);
            }
            else if (_frontendScene.State == Hud.FrontendScene.ScreenState.SinglePlayer
                  || _frontendScene.State == Hud.FrontendScene.ScreenState.MainMenuToSp
                  || _frontendScene.State == Hud.FrontendScene.ScreenState.SinglePlayerToMm)
            {
                // Phase 27-SP-FLYOUT-FIX3 — per-button asp render via
                // art_mapping.gas. NEW GAME / LOAD GAME each render
                // through DrawMenubarsButton, BACK through
                // DrawSpBackButton. One asp draw per widget with ONLY
                // its art_mapping-specified subsets — adjacent atlas
                // regions can't bleed because their subsets are masked.
                // SC-SP-HOVER — DrawMenubarsButton is the source of truth
                // for NEW GAME / LOAD GAME at the SETTLED pose (chrome's
                // DrawSpChrome menubars mask is chrome-only there; text
                // labels live on subsets 12/14 which render through this
                // per-widget call so hover swaps to menubars-up +
                // text-menubars1-up work). SC-MAINMENU-FOLD-LABELS — during
                // the MM↔SP transitions the labels render inside
                // DrawSpChrome's menubars pass instead, riding the folding
                // planks with the clip; firing these settled-pose draws
                // mid-flight would paint the labels at the destination
                // while the wood is still turning.
                bool spSettled = _frontendScene.State == Hud.FrontendScene.ScreenState.SinglePlayer;
                if (spSettled && _spMenu.TryGetButtonStateAndRect(
                        Hud.SinglePlayerMenuPanel.Action.NewGame,
                        viewportW, viewportH,
                        out int ngx, out int ngy, out int ngw, out int ngh,
                        out bool ngHover, out bool ngPress))
                {
                    _frontendScene.DrawMenubarsButton(viewportW, viewportH,
                        ngx, ngy, ngw, ngh, ngHover, ngPress, "button_start_new_game");
                }
                if (spSettled && _spMenu.TryGetButtonStateAndRect(
                        Hud.SinglePlayerMenuPanel.Action.LoadGame,
                        viewportW, viewportH,
                        out int lgx, out int lgy, out int lgw, out int lgh,
                        out bool lgHover, out bool lgPress))
                {
                    _frontendScene.DrawMenubarsButton(viewportW, viewportH,
                        lgx, lgy, lgw, lgh, lgHover, lgPress, "button_load_game");
                }
                // SC-SP-BACK-FIX — only fire per-widget overlay when
                // hovered/pressed. Chrome's DrawSpChrome backbutton
                // draw (with state-aware e2b / b2e clip) is the
                // mouseout source of truth; per-widget renders the
                // hover/press swap on top.
                if (_spMenu.TryGetButtonStateAndRect(
                        Hud.SinglePlayerMenuPanel.Action.Back,
                        viewportW, viewportH,
                        out int bx, out int by, out int bw, out int bh,
                        out bool bHover, out bool bPress) && (bHover || bPress))
                {
                    _frontendScene.DrawSpBackButton(viewportW, viewportH,
                        bx, by, bw, bh, bHover, bPress);
                }
                // Hover overlays for the SP submenu buttons (only meaningful
                // in the settled SinglePlayer state where _spMenu has
                // hover state).
                if (_frontendScene.State == Hud.FrontendScene.ScreenState.SinglePlayer)
                    DrawSpMenuHoverOverlays(viewportW, viewportH);
            }
            else if (_frontendScene.State == Hud.FrontendScene.ScreenState.SinglePlayerToLg
                  || _frontendScene.State == Hud.FrontendScene.ScreenState.LoadGame
                  || _frontendScene.State == Hud.FrontendScene.ScreenState.LgToSinglePlayer)
            {
                // SC-MAINMENU-LOADGAME — the Load Game chrome (title drum,
                // map window, menubars, PREVIOUS/NEXT plates) is fully drawn
                // by _frontendScene.Draw above. Only the hover/press texture
                // swaps for the PREVIOUS / NEXT plates layer on here, once
                // the screen has settled (mid-flight plates aren't clickable,
                // matching the SP transition input rule).
                if (_frontendScene.State == Hud.FrontendScene.ScreenState.LoadGame
                    && _loadDialog.IsOpen && _loadDialog.MainMenuStyle)
                {
                    if (_loadDialog.PrevHovered || _loadDialog.PrevPressed)
                        _frontendScene.DrawPreviousButton(viewportW, viewportH, 0, 0, 0, 0,
                            _loadDialog.PrevHovered, _loadDialog.PrevPressed);
                    if (_loadDialog.NextHovered || _loadDialog.NextPressed)
                        _frontendScene.DrawNextButton(viewportW, viewportH, 0, 0, 0, 0,
                            _loadDialog.NextHovered, _loadDialog.NextPressed);
                }
            }
            else if (_frontendScene.State == Hud.FrontendScene.ScreenState.SinglePlayerToCd
                  || _frontendScene.State == Hud.FrontendScene.ScreenState.CharacterSelect
                  || _frontendScene.State == Hud.FrontendScene.ScreenState.CharacterSelectToSp)
            {
                // SC-CD-PREVNEXT-FIX — explicit DrawPreviousButton /
                // DrawNextButton calls were doing a SECOND full
                // backbutton.asp draw on top of the chrome's already-
                // rendered backbutton (DrawCdChrome's DrawMesh("backbutton",
                // ...) line). Both stretched the same mesh with the same
                // backbutton_b2pn clip but at different visual rects
                // (visualWMul=5, visualHMul=2 on the per-widget draw vs
                // shared-scene projection on the chrome draw). Result:
                // visible "buttons overlapping buttons" only on option
                // 94 because option 62's modal path never called these.
                // Removed: the chrome's backbutton draw is the single
                // source of truth for prev/next visuals; _csMenu
                // hit-rects + DrawCsMenuHoverOverlays' translucent
                // hover tint provide the click-feedback layer.
                if (_frontendScene.State == Hud.FrontendScene.ScreenState.CharacterSelect)
                {
                    DrawCsMenuHoverOverlays(viewportW, viewportH);
                    // Phase 29-CD-CREATOR-FIX2 — Reset() moved to the
                    // state-edge handler in OnUpdate so HeroName isn't
                    // clobbered every frame while typing.
                    // Build hero preview lazily once the deps are ready
                    // (LoadPlayActors populates _skinShader / _templateStore /
                    // _playResolver — but at boot only _playResolver is set,
                    // so the preview renders empty until a region loads).
                    if (_heroPreview is null && _gl is not null && _skinShader is not null
                        && _templateStore is not null && _playResolver is not null)
                    {
                        _heroPreview = new HeroPreviewRenderer(
                            _gl, _skinShader, _templateStore, _playResolver,
                            ResolveActorTexture);
                    }
                    if (_heroPreview is not null)
                    {
                        _heroPreview.EnsurePreview(_creator.Picker);
                        _heroPreview.Tick(dt);
                        _heroPreview.Draw(viewportW, viewportH);
                    }
                    // Per-button asp render + hover overlays + name overlay.
                    DrawCharacterCreatorOverlays(viewportW, viewportH);
                }
            }
            else if (_frontendScene.State == Hud.FrontendScene.ScreenState.CharacterSelectToDifficulty
                  || _frontendScene.State == Hud.FrontendScene.ScreenState.Difficulty
                  || _frontendScene.State == Hud.FrontendScene.ScreenState.DifficultyToCharacterSelect)
            {
                // Per-button hover/press flags. Only valid in settled
                // Difficulty state; transitions render with no hover.
                bool eHov = false, ePr = false;
                bool mHov = false, mPr = false;
                bool hHov = false, hPr = false;
                bool bkHov = false, bkPr = false;
                if (_frontendScene.State == Hud.FrontendScene.ScreenState.Difficulty)
                {
                    _diffMenu.TryGetButtonStateAndRect(Hud.DifficultyMenuPanel.Action.Easy,
                        viewportW, viewportH, out _, out _, out _, out _, out eHov, out ePr);
                    _diffMenu.TryGetButtonStateAndRect(Hud.DifficultyMenuPanel.Action.Medium,
                        viewportW, viewportH, out _, out _, out _, out _, out mHov, out mPr);
                    _diffMenu.TryGetButtonStateAndRect(Hud.DifficultyMenuPanel.Action.Hard,
                        viewportW, viewportH, out _, out _, out _, out _, out hHov, out hPr);
                    _diffMenu.TryGetButtonStateAndRect(Hud.DifficultyMenuPanel.Action.Back,
                        viewportW, viewportH, out _, out _, out _, out _, out bkHov, out bkPr);
                }
                // Chrome subset hover/press swap for each difficulty
                // button — paint menubars-up/-down on top of the
                // chrome's mouseout state. Same pattern as cd's
                // prev/next + SP submenu's NEW GAME / LOAD GAME.
                if (eHov || ePr) _frontendScene.DrawDifficultyButton(viewportW, viewportH, eHov, ePr, "button_easy");
                if (mHov || mPr) _frontendScene.DrawDifficultyButton(viewportW, viewportH, mHov, mPr, "button_medium");
                if (hHov || hPr) _frontendScene.DrawDifficultyButton(viewportW, viewportH, hHov, hPr, "button_hard");
                // Engraved EASY / NORMAL / HARD labels via UV-cropped
                // IconRenderer; per-row hover/press swaps text-menubars3
                // → -up / -down so only the hovered row's label glows.
                if (_iconRenderer is not null)
                    _frontendScene.TryDrawDifficultyLabels(viewportW, viewportH, _iconRenderer,
                        eHov, ePr, mHov, mPr, hHov, hPr);
                // BACK button hover/press overlay (chrome owns mouseout).
                if (bkHov || bkPr)
                {
                    if (_diffMenu.TryGetButtonStateAndRect(
                            Hud.DifficultyMenuPanel.Action.Back,
                            viewportW, viewportH,
                            out int bx, out int by, out int bw, out int bh,
                            out _, out _))
                    {
                        _frontendScene.DrawDiffBackButton(viewportW, viewportH,
                            bx, by, bw, bh, bkHov, bkPr);
                    }
                }
            }
            else if (_frontendScene.State == Hud.FrontendScene.ScreenState.Multiplayer)
            {
                // SC-MP-MENU — provider-button hover/press overlays +
                // engraved labels. ZoneMatch renders dimmed inside
                // TryDrawMultiplayerLabels and has no hover (excluded
                // from panel hit-testing — the service is discontinued).
                _mpMenu.TryGetButtonStateAndRect(Hud.MultiplayerMenuPanel.Action.Internet,
                    viewportW, viewportH, out _, out _, out _, out _, out bool netHov, out bool netPr);
                _mpMenu.TryGetButtonStateAndRect(Hud.MultiplayerMenuPanel.Action.Network,
                    viewportW, viewportH, out _, out _, out _, out _, out bool lanHov, out bool lanPr);
                _mpMenu.TryGetButtonStateAndRect(Hud.MultiplayerMenuPanel.Action.Back,
                    viewportW, viewportH, out int mbx, out int mby, out int mbw, out int mbh,
                    out bool mbHov, out bool mbPr);
                if (netHov || netPr) _frontendScene.DrawMultiplayerButton(viewportW, viewportH, netHov, netPr, "button_internet");
                if (lanHov || lanPr) _frontendScene.DrawMultiplayerButton(viewportW, viewportH, lanHov, lanPr, "button_lan");
                if (_iconRenderer is not null)
                    _frontendScene.TryDrawMultiplayerLabels(viewportW, viewportH, _iconRenderer,
                        netHov, netPr, lanHov, lanPr);
                if (mbHov || mbPr)
                    _frontendScene.DrawSpBackButton(viewportW, viewportH,
                        mbx, mby, mbw, mbh, mbHov, mbPr);
            }
            else if (_frontendScene.State == Hud.FrontendScene.ScreenState.MpInternet
                  || _frontendScene.State == Hud.FrontendScene.ScreenState.MpNetwork)
            {
                // SC-MP-SESSION — the authored parchment session screen.
                if (_iconRenderer is not null && _barRenderer is not null && _textRenderer is not null)
                    _mpSession.Draw(viewportW, viewportH,
                        _iconRenderer, _barRenderer, _textRenderer,
                        TryGetGuiTexture, _mpLocalIp ??= LocalIpBestEffort());
            }
            else if (_frontendScene.State == Hud.FrontendScene.ScreenState.MpStaging)
            {
                // SC-MP-EOS P8 — staging area (roster/chat/settings).
                if (_iconRenderer is not null && _barRenderer is not null && _textRenderer is not null)
                    _mpStaging.Draw(viewportW, viewportH,
                        _iconRenderer, _barRenderer, _textRenderer, TryGetGuiTexture);
            }
            // Phase 26-VIEWPORT — disable scissor so any HUD layers that
            // intentionally fill the framebuffer (e.g. About overlay's
            // 60% black scrim) aren't clipped to the chrome letterbox.
            _gl?.Disable(EnableCap.ScissorTest);
            // Sword cursor is drawn at the END of this pass (below), so it
            // sits on top of the Options dialog / Load overlay that render
            // above the frontend chrome.
        }
        // SC-OPTIONS-CHROME — Options dialog must draw in boot mode too
        // (was only firing in the in-game render block at line ~10808
        // which DrawBootScene's early-return skips). Without this,
        // clicking Options on the Main Menu opens the dialog but it
        // never renders → "nothing happens when I click it."
        if (_optionsMenu.IsOpen && _barRenderer is not null && _textRenderer is not null)
        {
            _optionsMenu.Draw(_barRenderer, _textRenderer, _iconRenderer, _frontendScene, viewportW, viewportH,
                commonChrome: GetCommonTexture);
        }
        // SC-MAINMENU-LOADGAME — the Load screen's 2D layer (save list, info
        // text, thumbnail, LOAD/DELETE) over the mesh window. Waits for the
        // settled LoadGame state so the widgets don't float in mid-air while
        // the window is still dropping in / flying out.
        if (_loadDialog.IsOpen && _loadDialog.MainMenuStyle && _barRenderer is not null && _gl is not null
            && _textRenderer is not null
            && _frontendScene.State == Hud.FrontendScene.ScreenState.LoadGame)
        {
            _loadDialog.Draw(_gl, _barRenderer, _textRenderer, _iconRenderer,
                             TryGetGuiTexture, GetCommonTexture, viewportW, viewportH);
        }
        // Phase 27-SP-FLYOUT — sword cursor in the frontend menu, drawn LAST so
        // it sits ON TOP of the Options dialog and Load-game overlay (both of
        // which render above the frontend chrome). Same FrontendWantsSpriteCursor
        // gate that hides the OS cursor, so the intro splash / sword-drop frames
        // keep the OS cursor and we don't double-render. Always the Pointer
        // state — combat / talk / loot icons make no sense outside gameplay.
        if (FrontendWantsSpriteCursor())
        {
            // DrawBootScene never reaches the in-game HUD pass that normally
            // draws the sprite cursor, so hide the OS arrow here — this sprite
            // is the only cursor the menu should show.
            EnsureOsCursorHidden();
            EnsureCursorTextures();
            if (_iconRenderer is not null && !_mouseLookActive && _cursorPointer is not null)
            {
                const int big = 64, hsBigX = 21, hsBigY = 13;
                int cx = (int)_currentMousePos.X - hsBigX;
                int cy = (int)_currentMousePos.Y - hsBigY;
                _iconRenderer.DrawIcon(viewportW, viewportH, _cursorPointer, cx, cy, big, big, Vector4.One);
            }
        }
        _textRenderer?.EndPass();
    }

    /// <summary>Phase 24-MAINMENU step 6 — placeholder About sub-screen.
    /// Modal dim + a centered card with title + copyright text + a "click
    /// anywhere to close" hint. Future splinter SC-MAINMENU-ABOUT-RAW will
    /// load the shipped <c>about_dialog.gas</c> layout + swap the card for
    /// DS1's wood-bordered chrome.</summary>
    private void DrawAboutOverlay(int viewportW, int viewportH)
    {
        if (_barRenderer is null || _textRenderer is null) return;
        // Modal scrim — same 60% black PauseMenu / OptionsMenu use, so
        // the About card reads as the topmost layer regardless of what's
        // behind it.
        _barRenderer.DrawRect(viewportW, viewportH, 0, 0, viewportW, viewportH,
            new Vector4(0f, 0f, 0f, 0.60f));
        // Centered card sized at 800x600-authored 480x240 (so it scales
        // proportionally with the menu chrome).
        float scale = MathF.Min(viewportH / 600f, viewportW / 800f);
        int fontScale = Math.Max(1, (int)MathF.Round(scale));
        int cw = (int)MathF.Round(480 * scale);
        int ch = (int)MathF.Round(240 * scale);
        int cx = (viewportW - cw) / 2;
        int cy = (viewportH - ch) / 2;
        _barRenderer.DrawRect(viewportW, viewportH, cx, cy, cw, ch,
            new Vector4(0.10f, 0.06f, 0.04f, 0.95f));
        // Hand-rolled 1px border (matches MainMenuPanel.DrawBorder).
        var border = new Vector4(0.65f, 0.50f, 0.30f, 1f);
        _barRenderer.DrawRect(viewportW, viewportH, cx,          cy,            cw, 1,  border);
        _barRenderer.DrawRect(viewportW, viewportH, cx,          cy + ch - 1,   cw, 1,  border);
        _barRenderer.DrawRect(viewportW, viewportH, cx,          cy,            1,  ch, border);
        _barRenderer.DrawRect(viewportW, viewportH, cx + cw - 1, cy,            1,  ch, border);
        var ink    = new Vector4(0.95f, 0.85f, 0.65f, 1f);
        var inkDim = new Vector4(0.65f, 0.55f, 0.40f, 1f);
        // Multi-line text. Plain copperplate, scale_aware vertical stride.
        var lines = new[]
        {
            "About SiegeFX",
            "",
            "Open-source Dungeon Siege 1 reimplementation.",
            "GPLv3-licensed; ships no copyrighted assets.",
            "Requires the original Dungeon Siege game data.",
            "",
            "Dungeon Siege © 2002 Gas Powered Games / Microsoft.",
            "",
            "(click anywhere or press Esc to close)",
        };
        int lineH = 16 * fontScale;
        int totalH = lines.Length * lineH;
        int ty = cy + (ch - totalH) / 2;
        for (int i = 0; i < lines.Length; i++)
        {
            var s = lines[i];
            int tw = _textRenderer.MeasureWidth(s, fontScale);
            int tx = cx + (cw - tw) / 2;
            var color = (i == 0 || i == lines.Length - 1) ? ink : inkDim;
            _textRenderer.DrawString(viewportW, viewportH, s, tx, ty + i * lineH, color, fontScale);
        }
    }

    /// <summary>Phase 27-SP-FLYOUT — translucent white overlay on the
    /// hovered main menu button so the cursor leaves a visual trail
    /// over the wood-button chrome. Pressed buttons get a darker
    /// overlay so press is visible too. Alphas tuned to read clearly
    /// over the wood-orange chrome (0.12 was too subtle on the
    /// engraved label per user feedback).</summary>
    private void DrawMainMenuHoverOverlays(int viewportW, int viewportH)
    {
        // SC-MAIN-HOVER — flat alpha-rect tint replaced by per-widget
        // texture swap on the chrome (DrawExitButton on hover, mainmenu
        // body chrome handles non-EXIT buttons). The flat tint was
        // painting boxy rectangles over the engraved chrome — same
        // regression mode the cd / Difficulty / SP back chrome had.
        // Per-button chrome render for SinglePlayer/Multiplayer/etc.
        // is parked under SC-MAINMENU-BUTTONS-CHROME (currently
        // those use the menu chrome's natural draw with no hover swap).
    }

    /// <summary>Phase 27-SP-FLYOUT — was a flat alpha rect over the
    /// SP submenu buttons on hover/press. SC-SP-HOVER no-op'd it:
    /// the chrome's per-widget DrawMenubarsButton + DrawSpBackButton
    /// calls already swap textures for the proper engraved hover/press
    /// look (menubars-up/-down + text-menubars1-up/-down + exitback-up/-down
    /// + text-small-up/-down per art_mapping.gas). Function kept as
    /// stub so callers don't break.</summary>
    private void DrawSpMenuHoverOverlays(int viewportW, int viewportH) { }

    /// <summary>Phase 29-CD-CREATOR — render the 12 spinner-arrow
    /// buttons (per art_mapping.gas) + their hover overlays + the
    /// typed-name TextRenderer overlay. Caller must already have
    /// drawn the chrome (FrontendScene) and the live hero preview
    /// (HeroPreviewRenderer). Used both from the boot CharacterSelect
    /// chrome render block and from the in-region _creator.IsOpen
    /// block — same overlay layer in both cases.</summary>
    private void DrawCharacterCreatorOverlays(int viewportW, int viewportH)
    {
        if (_frontendScene is null) return;
        // SC-CD-PREVNEXT-HOVER — fire texture-swap overlay only when
        // hovered or pressed. Chrome's DrawCdChrome backbutton draw is
        // the mouseout base; per-widget overlay paints exitback-up /
        // exitback-down + text-small-up / text-small-down on top when
        // the user is on the button. Same pattern as DrawHeromenuButton
        // for the spinner arrows.
        if (_csMenu.TryGetButtonStateAndRect(
                Hud.CharacterSelectMenuPanel.Action.Previous,
                viewportW, viewportH,
                out int pX, out int pY, out int pW, out int pH,
                out bool pHov, out bool pPr) && (pHov || pPr))
        {
            _frontendScene.DrawPreviousButton(viewportW, viewportH,
                pX, pY, pW, pH, pHov, pPr);
        }
        if (_csMenu.TryGetButtonStateAndRect(
                Hud.CharacterSelectMenuPanel.Action.Next,
                viewportW, viewportH,
                out int nX, out int nY, out int nW, out int nH,
                out bool nHov, out bool nPr) && (nHov || nPr))
        {
            _frontendScene.DrawNextButton(viewportW, viewportH,
                nX, nY, nW, nH, nHov, nPr);
        }
        var actions = new[]
        {
            (Hud.CharacterCreatorPanel.Action.GenderLeft,  "button_gender_left"),
            (Hud.CharacterCreatorPanel.Action.GenderRight, "button_gender_right"),
            (Hud.CharacterCreatorPanel.Action.HeadLeft,    "button_head_left"),
            (Hud.CharacterCreatorPanel.Action.HeadRight,   "button_head_right"),
            (Hud.CharacterCreatorPanel.Action.FaceLeft,    "button_face_left"),
            (Hud.CharacterCreatorPanel.Action.FaceRight,   "button_face_right"),
            (Hud.CharacterCreatorPanel.Action.HairLeft,    "button_hair_left"),
            (Hud.CharacterCreatorPanel.Action.HairRight,   "button_hair_right"),
            (Hud.CharacterCreatorPanel.Action.ShirtLeft,   "button_shirt_left"),
            (Hud.CharacterCreatorPanel.Action.ShirtRight,  "button_shirt_right"),
            (Hud.CharacterCreatorPanel.Action.PantsLeft,   "button_pants_left"),
            (Hud.CharacterCreatorPanel.Action.PantsRight,  "button_pants_right"),
        };
        foreach (var (act, widget) in actions)
        {
            if (!_creator.TryGetButtonStateAndRect(act, viewportW, viewportH,
                    out int x, out int y, out int w, out int h,
                    out bool hov, out bool pr)) continue;
            _frontendScene.DrawHeromenuButton(viewportW, viewportH,
                x, y, w, h, hov, pr, widget);
        }
        // Flat _barRenderer hover/press tint removed. heromenu-up.raw and
        // heromenu-down.raw already bake the proper arrow-tip glow shape
        // (curved, only at the actual ◄ ► geometry); painting a flat
        // alpha rectangle on top stretches that glow into a boxy bar
        // across the whole 102px half-bar. Texture swap stands alone.
        // Engraved row labels (GENDER / HEAD / SKIN / HAIR / SHIRT /
        // PANTS) cropped out of text-small.raw and rendered via
        // IconRenderer.DrawIcon's UV-cropped overload. Atlas layout
        // (visual top-down, after IconRenderer's V flip): NEXT/PREV,
        // NAME/HERO, GENDER/HEAD, SKIN/HAIR, SHIRT/PANTS, EXIT/BACK.
        // Each row is ~16.7% atlas height; left column U[0,0.5],
        // right column U[0.5,1.0]. UV ranges are estimates — refine
        // empirically. Per-label rect width sized to keep aspect
        // matching the atlas region (visual w / visual h).
        var textSmall = _frontendScene.GetChromeTexture("text-small-up");
        if (textSmall is not null && _iconRenderer is not null)
        {
            // Visual UV coords (IconRenderer flips internally for the
            // bottom-up raw). Atlas top-down: EXIT/BACK, SHIRT/PANTS,
            // SKIN/HAIR, GENDER/HEAD, NAME/HERO, PREVIOUS/NEXT — each
            // row ~16.7% of atlas height. Left column U[0, 0.5], right
            // column U[0.5, 1.0].
            (string label, float u0, float v0, float u1, float v1)[] labelUVs =
            {
                ("GENDER", 0.00f, 0.46f, 0.50f, 0.58f),
                ("HEAD",   0.50f, 0.46f, 1.00f, 0.58f),
                ("SKIN",   0.00f, 0.32f, 0.50f, 0.48f),
                ("HAIR",   0.50f, 0.32f, 1.00f, 0.48f),
                ("SHIRT",  0.00f, 0.16f, 0.50f, 0.32f),
                ("PANTS",  0.50f, 0.16f, 1.00f, 0.32f),
            };
            int[] rowGasY = { 206, 256, 306, 355, 399, 450 };
            float scale = MathF.Min(viewportH / 600f, viewportW / 800f);
            int authoredW = (int)MathF.Round(800 * scale);
            int authoredH = (int)MathF.Round(600 * scale);
            int dx = (viewportW - authoredW) / 2;
            int dy = (viewportH - authoredH) / 2;
            const int labelGasW = 70;
            const int labelGasH = 18;
            const int barCenterGasX = 277;
            const int rowHeightGas = 20;
            var tint = new Vector4(1f, 1f, 1f, 1f);
            for (int i = 0; i < labelUVs.Length; i++)
            {
                int w = (int)MathF.Round(labelGasW * scale);
                int h = (int)MathF.Round(labelGasH * scale);
                int cx = dx + (int)MathF.Round(barCenterGasX * scale);
                int cy = dy + (int)MathF.Round((rowGasY[i] + rowHeightGas * 0.5f) * scale);
                _iconRenderer.DrawIcon(viewportW, viewportH, textSmall,
                    cx - w / 2, cy - h / 2, w, h, tint,
                    labelUVs[i].u0, labelUVs[i].v0, labelUVs[i].u1, labelUVs[i].v1);
            }
        }

        // SC-CD-CHOOSEHERO — title plaque renders via mainmenu.asp's
        // cd-state pose draw inside DrawCdChrome (subset 0 = plaque).
        // The label subsets 1+2 (text-01L "CHOOSE" / text-01R "HERO")
        // park off-screen above at sng2cd@1.0 per trace-pose, so we
        // overlay them via direct UV crops of text-01L.raw / text-01R.raw
        // — same trick the row labels use.
        if (_iconRenderer is not null)
        {
            var texL = _frontendScene.GetChromeTexture("text-01L");
            var texR = _frontendScene.GetChromeTexture("text-01R");
            if (texL is not null && texR is not null)
            {
                float scale = MathF.Min(viewportH / 600f, viewportW / 800f);
                int authoredH = (int)MathF.Round(600 * scale);
                int dy = (viewportH - authoredH) / 2;
                const int titleGasY  = 60;
                const int titleGasH  = 36;
                const int chooseGasW = 180; // text-01L slice ~5:1
                const int heroGasW   = 144; // text-01R slice ~4:1
                int chooseW = (int)MathF.Round(chooseGasW * scale);
                int chooseH = (int)MathF.Round(titleGasH  * scale);
                int heroW   = (int)MathF.Round(heroGasW   * scale);
                int heroH   = chooseH;
                int totalW  = chooseW + heroW;
                int chooseX = (viewportW - totalW) / 2;
                int chooseY = dy + (int)MathF.Round(titleGasY * scale);
                int heroX   = chooseX + chooseW;
                int heroY   = chooseY;
                var tint = new Vector4(1f, 1f, 1f, 1f);
                // CHOOSE row = middle of text-01L atlas (5 stacked labels).
                _iconRenderer.DrawIcon(viewportW, viewportH, texL,
                    chooseX, chooseY, chooseW, chooseH, tint,
                    0.00f, 0.40f, 1.00f, 0.60f);
                // HERO row = middle of text-01R atlas (5 stacked labels).
                _iconRenderer.DrawIcon(viewportW, viewportH, texR,
                    heroX, heroY, heroW, heroH, tint,
                    0.00f, 0.40f, 1.00f, 0.60f);
            }
        }

        // Typed-name overlay at the gas-authored name_edit_box rect.
        // Phase 29-CD-CREATOR-FIX — bump font scale by 1 (12px → ~24px
        // → 36px for clearer reading on the name plate, per user feedback)
        // and left-align inside the rect (text sits to the LEFT of the
        // image plate it's drawn over, not centered).
        if (_textRenderer is not null && _textRenderer.HasFont)
        {
            var nameRect = _creator.NameRectInViewport(viewportW, viewportH);
            string typed = string.IsNullOrEmpty(_creator.HeroName) ? "" : _creator.HeroName;
            int fontScale = _creator.FontScale + 1;
            // DS1's typed hero name is plain white (verified against
            // _scratch_creator/ref_character_select.png — "Zingter" is
            // single-tone white). The metallic two-tone styling is for
            // the engraved labels (GENDER/HEAD/SKIN/HAIR/SHIRT/PANTS
            // and HERO), which come from text-small.raw via the chrome.
            var ink = new Vector4(1.00f, 0.98f, 0.92f, 1f);
            int padX = Math.Max(2, fontScale * 2);
            if (typed.Length == 0)
            {
                _barRenderer?.DrawRect(viewportW, viewportH,
                    nameRect.X + padX, nameRect.Y + nameRect.H - 2,
                    nameRect.W - padX * 2, 1, new Vector4(0.65f, 0.55f, 0.40f, 0.75f));
            }
            else
            {
                int th = (_textRenderer.Font?.Height ?? 12) * fontScale;
                int tx = nameRect.X + padX;
                int ty = nameRect.Y + (nameRect.H - th) / 2;
                _textRenderer.DrawString(viewportW, viewportH, typed, tx, ty, ink, fontScale);
            }
        }
    }

    /// <summary>Phase 28-CD-FLYOUT — was a flat _barRenderer alpha rect
    /// over the prev/next hit areas on hover/press. SC-CD-PREVNEXT-HOVER
    /// turned this into a no-op: the chrome texture-swap (DrawPreviousButton
    /// / DrawNextButton with exitback-up/-down + text-small-up/-down per
    /// art_mapping.gas) is the real engraved highlight; the flat tint
    /// painted a boxy rectangle over it that overrode the authored shape,
    /// matching the same regression we saw on the spinner arrows. Function
    /// kept as a stub so callers don't break.</summary>
    private void DrawCsMenuHoverOverlays(int viewportW, int viewportH) { }

    /// <summary>Phase 27-SP-FLYOUT-FIX — render the SP submenu's two
    /// button labels via TextRenderer. Replaces the masked-off
    /// menubars.asp text subsets (6-15) which bled adjacent atlas
    /// rows onto the visible slots at SP pose. Same overlay-on-wood
    /// approach as <c>DrawBackButton</c>; centered in each slot rect
    /// at the same font-scale the panel uses for hit testing so
    /// label position tracks viewport size.</summary>
    private void DrawSinglePlayerLabels(int viewportW, int viewportH)
    {
        if (_textRenderer is null || !_textRenderer.HasFont) return;
        int fontScale = _spMenu.FontScale;
        var ink = new Vector4(0.95f, 0.85f, 0.65f, 1f);
        DrawSlotLabel(Hud.SinglePlayerMenuPanel.Action.NewGame,  "START NEW GAME", fontScale, ink, viewportW, viewportH);
        DrawSlotLabel(Hud.SinglePlayerMenuPanel.Action.LoadGame, "LOAD GAME",      fontScale, ink, viewportW, viewportH);
    }

    private void DrawSlotLabel(Hud.SinglePlayerMenuPanel.Action a, string label,
                               int fontScale, Vector4 ink, int viewportW, int viewportH)
    {
        if (_textRenderer is null) return;
        if (!_spMenu.TryGetButtonStateAndRect(a, viewportW, viewportH,
                out int x, out int y, out int w, out int h, out _, out _)) return;
        int tw = _textRenderer.MeasureWidth(label, fontScale);
        int th = (_textRenderer.Font?.Height ?? 12) * fontScale;
        int tx = x + (w - tw) / 2;
        int ty = y + (h - th) / 2;
        _textRenderer.DrawString(viewportW, viewportH, label, tx, ty, ink, fontScale);
    }

    /// <summary>Helper for <see cref="DrawBootScene"/>: resolve a splash
    /// RAW by basename through the play resolver, cache the upload, and
    /// blit at the authored rect (scale-translated to viewport pixels).
    /// Silent no-op when the texture can't be resolved so a missing asset
    /// just leaves a black hole rather than crashing the boot path.</summary>
    private void DrawSplashPanel(string texName, int ax, int ay, int aw, int ah,
                                 float scale, int dx, int dy, float alpha,
                                 int viewportW, int viewportH)
    {
        if (_iconRenderer is null || _gl is null || _playResolver is null) return;
        if (!_splashTexCache.TryGetValue(texName, out var tex))
        {
            try
            {
                if (!_playResolver.TryLoadByBasename(texName + ".raw", out var bytes))
                {
                    _splashTexCache[texName] = null!;
                    Console.Error.WriteLine($"  boot: splash tex '{texName}.raw' not found");
                    return;
                }
                var raw = SiegeFX.Core.Assets.RawImage.Load(bytes);
                tex = new GlTexture(_gl, raw);
                _splashTexCache[texName] = tex;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  boot: splash tex '{texName}' load failed — {ex.Message}");
                return;
            }
        }
        if (tex is null) return;
        int rx = dx + (int)MathF.Round(ax * scale);
        int ry = dy + (int)MathF.Round(ay * scale);
        int rw = (int)MathF.Round(aw * scale);
        int rh = (int)MathF.Round(ah * scale);
        _iconRenderer.DrawIcon(viewportW, viewportH, tex, rx, ry, rw, rh,
            new Vector4(1f, 1f, 1f, alpha));
    }

    /// <summary>Phase 22-A SC-HUD-DATABAR — render the always-on bottom-row
    /// HUD button strip. Called from the HUD pass. Lazy-loads textures on
    /// first call. Each button picks its texture from up/dwn/hov based on
    /// its current hover/press state; pause/play swap-pair gates on
    /// <see cref="_isPaused"/>; labels gates on <see cref="_overheadLabelsVisible"/>.</summary>
    private void DrawDataBar(int viewportW, int viewportH)
    {
        if (_iconRenderer is null) return;
        EnsureDataBarTextures();

        // Dockbar background — stretches full viewport width. Gas authors
        // uvcoords = 0,0,1,0.861 in DS1's bottom-up frame (covers the
        // bottom 86% of the texture, leaving the top 14% as padding).
        // Convert to screen-top-down convention via the same V-flip rule
        // documented on the per-button draw below.
        if (_dbStatusbarBg is not null)
        {
            var (bx, by, bw, bh) = Hud.DataBar.ProjectBgRect(viewportW, viewportH);
            _iconRenderer.DrawIcon(viewportW, viewportH, _dbStatusbarBg,
                bx, by, bw, bh, Vector4.One,
                0f, 1f - 0.861f, 1f, 1f - 0f);
        }

        // Per-button render. Pick the right texture for each state.
        // SC-MEGAMAP wire-up — the map button is LIVE now (the overview
        // camera routes through the live chase pose), so the disable_color
        // dim comes off.
        foreach (var slot in Hud.DataBar.Slots)
        {
            GlTexture? tex = ResolveDataBarTexture(slot.Id);
            if (tex is null) continue;
            var (x, y, w, h) = Hud.DataBar.ProjectRect(slot, viewportW, viewportH);
            var tint = Vector4.One;
            // Apply the gas-authored uvcoords so the rendered rect samples
            // only the artwork portion of the 32×32 texture (the rest is
            // transparent padding). Default UV (0,0,1,1) would stretch the
            // padding INTO the rect — user reported potion bottles looked
            // stretched vertically because the 22×32 rect was sampling a
            // 32×32 texture without crop.
            // V-AXIS NOTE: DS1 RAWs are stored bottom-up (see
            // project_siegefx_raw_bottomup.md), and the gas's uvcoords V
            // values are authored in that same bottom-up frame: gas v0 is
            // the BOTTOM of the texture region and gas v1 is the TOP.
            // IconRenderer.DrawIcon takes vMin/vMax in screen-top-down
            // convention (vMin = top of rect → top of visible image). So
            // to convert: screenVMin = 1 - gasV1, screenVMax = 1 - gasV0.
            // U is unaffected (no horizontal flip convention). Without
            // this flip the pause-button's V=0.125 bottom-padding crop
            // displayed as a TOP crop, cutting into the visible icon.
            _iconRenderer.DrawIcon(viewportW, viewportH, tex, x, y, w, h, tint,
                slot.U0, 1f - slot.V1, slot.U1, 1f - slot.V0);
        }

        // SC-PAUSE-FLASH — DS1 flashes the data-bar play button while the game
        // is paused (manual pause OR the Esc menu) to signal "press to resume".
        // Re-draw the play icon over its slot with a pulsing alpha; the base
        // button (already the play glyph via ResolveDataBarTexture) stays solid
        // underneath so the glyph never fully vanishes.
        if (PausedForDisplay && _dbPlayUp is not null)
        {
            _pausePulseT += (float)_frameDtSeconds;
            float pulse = 0.35f + 0.35f * MathF.Sin(_pausePulseT * 6.5f); // 0..0.7
            var pauseSlot = Hud.DataBar.Slots.FirstOrDefault(sl => sl.Id == Hud.DataBar.ButtonId.Pause);
            if (pauseSlot.Id == Hud.DataBar.ButtonId.Pause)
            {
                var (px, py, pw, ph) = Hud.DataBar.ProjectRect(pauseSlot, viewportW, viewportH);
                var glow = _dbPlayHov ?? _dbPlayUp;
                _iconRenderer.DrawIcon(viewportW, viewportH, glow, px, py, pw, ph,
                    new Vector4(1f, 1f, 1f, pulse),
                    pauseSlot.U0, 1f - pauseSlot.V1, pauseSlot.U1, 1f - pauseSlot.V0);
            }
        }
        else _pausePulseT = 0f;

        // Quest indicator flash overlay — pulses red over the quest_log
        // button when a quest activated/completed in the recent past.
        if (_questIndicatorFlashRemaining > 0f && _dbBookRed is not null)
        {
            // Pulse alpha via a slow sine on the remaining timer.
            float t = _questIndicatorFlashRemaining;
            float pulse = 0.5f + 0.5f * MathF.Sin(t * 12f);
            var tint = new Vector4(1f, 1f, 1f, pulse);
            // Authored rect 556,423,620,487 (64×64 ring around the book
            // icon), right_anchor=84. Project against viewport.
            var indicator = new Hud.DataBar.Slot(
                Hud.DataBar.ButtonId.QuestLog,
                556, 423, 64, 64, rightAnchor: 84);
            var (ix, iy, iw, ih) = Hud.DataBar.ProjectRect(indicator, viewportW, viewportH);
            _iconRenderer.DrawIcon(viewportW, viewportH, _dbBookRed, ix, iy, iw, ih, tint);
        }

        // INFORAIL — data_bar rollover_help tooltip. Gas:273 authors
        // text_box_info rect 95,450,501,479 (centered between the two
        // button clusters), font b_gui_fnt_12p_copperplate-light,
        // justify=center. We render the DS1-authentic tooltip string
        // (DataBar.TooltipFor) for whichever button the cursor is over.
        var hoveredId = _dataBar.CurrentHover;
        if (hoveredId is not null && _textRenderer is not null)
        {
            string tip = Hud.DataBar.TooltipFor(
                hoveredId.Value, isPaused: _isPaused, labelsOn: _overheadLabelsVisible);
            if (!string.IsNullOrEmpty(tip))
            {
                // text_box_info authors rect 95,450,501,479 in the 640×480
                // ref — "centered between the two button clusters" on 4:3.
                // Our widescreen bar splits the clusters to the viewport
                // edges (left cluster left-anchored, right cluster right-
                // anchored), so the authored X band strands the help text
                // by the left cluster where it goes unnoticed. Keep the
                // authored bottom band; center the TEXT on the viewport,
                // which is where "between the clusters" lands on any width.
                float dbScale = Hud.HudScale.Hud(viewportH);
                int tipY0 = viewportH - (int)Math.Round((480 - 450) * dbScale);
                int tipH  = (int)Math.Round((479 - 450) * dbScale);
                int textW = _textRenderer.MeasureWidth(tip);
                int tx = (viewportW - textW) / 2;
                int ty = tipY0 + (tipH - 8) / 2;
                var ink = new Vector4(0.86f, 0.83f, 0.69f, 1f);
                _textRenderer.DrawString(viewportW, viewportH, tip, tx, ty, ink);
            }
        }
    }

    /// <summary>Phase 27 — DS1 member_labels: each party member's screen_name
    /// floating over their head. SC-COMMANDS: gated by the authored
    /// [toggle_player_labels] "Character Labels" binding (L key,
    /// <see cref="_charLabelsVisible"/>) — a separate toggle from the
    /// Alt ground-item labels.</summary>
    private void DrawMemberLabels(int viewportW, int viewportH)
    {
        if (!_charLabelsVisible || _barRenderer is null || _textRenderer is null || _party.Count == 0) return;
        var ink = new Vector4(0.92f, 0.88f, 0.76f, 1f);
        foreach (var m in _party)
        {
            if (m.IsDead) continue;
            var head = m.CurrentTransform.Translation + new Vector3(0f, 2.2f, 0f);
            if (!ProjectToScreen(head, viewportW, viewportH, out int sx, out int sy)) continue;
            var name = ScreenNameOf(m.Actor.Template);
            if (string.IsNullOrEmpty(name)) continue;
            int lw = _textRenderer.MeasureWidth(name);
            _barRenderer.DrawRect(viewportW, viewportH, sx - lw / 2 - 2, sy - 1, lw + 4, 13,
                new Vector4(0f, 0f, 0f, 0.55f));
            _textRenderer.DrawString(viewportW, viewportH, name, sx - lw / 2, sy, ink);
        }
    }

    private void DrawCharacterAwp(int viewportW, int viewportH)
    {
        if (_player is null || _iconRenderer is null || _barRenderer is null || _textRenderer is null) return;
        // SC-HUD-DRAG — refresh the AWP + portrait-strip offsets from the
        // persisted (or in-drag) positions before either piece draws.
        ApplyHudPieceOffsets();
        if (!_awpLoaded)
        {
            _awpLoaded = true;
            _awpAtlas = TryGetGuiTexture("b_gui_ig_mnu_awp");
            _awpInvBtnTex    = TryGetGuiTexture("b_gui_ig_mnu_awp_buttons");
            _awpInvBtnHovTex = TryGetGuiTexture("b_gui_ig_mnu_awp_buttons-hov");
            _awpInvBtnDwnTex = TryGetGuiTexture("b_gui_ig_mnu_awp_buttons-dwn");
            if (!string.IsNullOrEmpty(_playerPortraitIconName))
                _awpPortraitTex = TryGetGuiTexture(_playerPortraitIconName);
            Console.WriteLine($"[char_awp] atlas: {(_awpAtlas is not null ? "ok" : "MISS")}, " +
                              $"portrait: {(_awpPortraitTex is not null ? "ok" : "MISS")} (name='{_playerPortraitIconName}'), " +
                              $"invbtn: {(_awpInvBtnTex is not null ? "ok" : "MISS")}, " +
                              $"hov: {(_awpInvBtnHovTex is not null ? "ok" : "MISS")}, " +
                              $"dwn: {(_awpInvBtnDwnTex is not null ? "ok" : "MISS")}");
        }
        if (_awpAtlas is null) return;
        // Hidden during NIS cinematics like the rest of the in-game HUD. The character AWP,
        // the team_portraits companion strip, and the field-commands panel all draw from here;
        // the companion strip in particular slips below the top letterbox bar into view. The
        // atlas still loads above, so there's no pop when the cinematic ends.
        if (_nisPhase != NisPhase.Off) return;
        var combat = _player.Actor.Combat;
        var stats  = _player.Actor.Stats;
        float hpFrac = stats.MaxLife > 0f ? combat.CurrentLife / stats.MaxLife : 0f;
        float mpFrac = stats.MaxMana > 0f ? combat.CurrentMana / stats.MaxMana : 0f;

        // INFORAIL — populate AWP slot icons by checking what's
        // actually equipped/slotted for each slot type:
        //   slot 1 (melee)  → icon only if equipped weapon's template
        //     chain hits weapon_melee
        //   slot 2 (ranged) → icon only if chain hits weapon_ranged
        //   slot 3 (primary spell)   → spell.InventoryIcon or null
        //   slot 4 (secondary spell) → spell.InventoryIcon or null
        // When the slot's item isn't present (no melee weapon, no
        // ranged weapon, no spell), null is passed and the frame
        // renders empty — matching DS1's "no item = no icon" rule.
        GlTexture? slot1 = ResolveAwpSlotByWeaponClass("weapon_melee");
        GlTexture? slot2 = ResolveAwpSlotByWeaponClass("weapon_ranged");
        // SC-AWP-SPELL-ICON — the active combat slot shows the spell's
        // [gui]active_icon (the small b_gui_ig_i_ic_sp_NNN art), NOT its
        // inventory_icon (the _inv spellbook-grid art). The companion strip
        // already resolves ActiveIcon; the player's own slots were reading
        // InventoryIcon, so they showed the wrong (spellbook) icon. Fall back
        // to InventoryIcon for spells that don't author an active_icon.
        GlTexture? slot3 = ResolveAwpSlotIcon(AwpSpellIcon(_playerSpellbook?.Primary));
        GlTexture? slot4 = ResolveAwpSlotIcon(AwpSpellIcon(_playerSpellbook?.Secondary));

        // SC-COMMANDS — W's expert mode folds the AWP with the same
        // authored min-mode transformation the open info-rail uses.
        bool railOpen = _charPanelOpen || _inventoryOpen ||
                        (_spellBookOpen && _spellbookOpenedWithI) || _awpExpertMode;
        // INFORAIL — per-slot skill progress fractions for the bottom-
        // up vertical fill behind each AWP slot icon. Slots 1/2 read
        // melee/ranged skill XP fractions; slots 3/4 mirror their
        // spell's caster-skill XP. Falls back to 0 when no progression
        // is bound (creator preview path).
        float sp1 = _progression?.SkillProgressFraction(SiegeFX.Core.Assets.SkillKind.Melee)  ?? 0f;
        float sp2 = _progression?.SkillProgressFraction(SiegeFX.Core.Assets.SkillKind.Ranged) ?? 0f;
        // Slots 3/4 track the caster skill of the spell ACTUALLY in each slot
        // (primary/secondary), not a fixed school — a nature primary must fill
        // its OWN slot's bar (not the secondary's), and an empty slot shows no
        // fill. Previously these were hardwired slot3=Combat / slot4=Nature, so
        // a nature primary lit the secondary box and left a phantom fill behind
        // an empty slot.
        float sp3 = SpellSlotProgress(_playerSpellbook?.Primary);
        float sp4 = SpellSlotProgress(_playerSpellbook?.Secondary);
        // SC-AWP-XP-PULSE / SC-AWP-LEVEL-HOLD — per-slot XP feedback:
        //  • a RISE in the fraction (kill/heal granted XP) fires the green tick;
        //  • a high→low DROP is a level-up reset → hold the bar at full for a
        //    beat (display only) so the level visibly completes before resetting.
        var spNow = new[] { sp1, sp2, sp3, sp4 };
        var spDisp = new float[4];
        for (int i = 0; i < 4; i++)
        {
            float now = spNow[i];
            float prev = _awpSlotProgPrev[i];
            if (_awpProgSeeded)
            {
                if (now > prev + 0.0004f)
                    _awpSlotXpPulse[i] = 1f;
                else if (prev > 0.55f && now < prev - 0.25f) // level-up reset
                    _awpSlotLevelHold[i] = AwpLevelHoldSeconds;
            }
            _awpSlotProgPrev[i] = now;
            if (_awpSlotXpPulse[i] > 0f)
                _awpSlotXpPulse[i] = MathF.Max(0f, _awpSlotXpPulse[i] - (float)_frameDtSeconds / AwpXpPulseSeconds);
            if (_awpSlotLevelHold[i] > 0f)
                _awpSlotLevelHold[i] = MathF.Max(0f, _awpSlotLevelHold[i] - (float)_frameDtSeconds);
            spDisp[i] = _awpSlotLevelHold[i] > 0f ? 1f : now;
        }
        _awpProgSeeded = true;
        // SC-DOWNED — leader portrait state tint (yellow ≤1/3 HP, red down).
        _characterAwp.PortraitTintColor = _player is null ? Vector4.One
            : Hud.TeamPortraits.PortraitTint(
                _player.Actor.Combat.Downed,
                _player.Actor.Stats.MaxLife > 0f
                    ? _player.Actor.Combat.CurrentLife / _player.Actor.Stats.MaxLife : 1f);
        _characterAwp.Draw(_iconRenderer, _barRenderer, viewportW, viewportH,
                           _awpAtlas, _playerPortraitLive ?? _awpPortraitTex, hpFrac, mpFrac, _activeAbilityIdx,
                           slot1, slot2, slot3, slot4, _awpInvBtnTex,
                           railOpen: railOpen,
                           inventoryBtnHovAtlas: _awpInvBtnHovTex,
                           inventoryBtnDwnAtlas: _awpInvBtnDwnTex,
                           hovered: _awpHover,
                           pressed: _awpPressed,
                           slot1Progress: spDisp[0], slot2Progress: spDisp[1],
                           slot3Progress: spDisp[2], slot4Progress: spDisp[3],
                           slot1Flash: _awpSlotXpPulse[0], slot2Flash: _awpSlotXpPulse[1],
                           slot3Flash: _awpSlotXpPulse[2], slot4Flash: _awpSlotXpPulse[3]);

        // Phase 27 — team_portraits strip: the follower cells stacked below
        // the leader's slot-1 portrait. One cell per recruited member.
        // SC-SUMMON-UI — the player's summon rides the strip too (retail's
        // "Summoned Creature" pseudo-member), so the gate must also open
        // when a summon is out with no companions recruited.
        if (_party.Count > 1 || PlayerSummon is not null)
        {
            _awpDeathTex ??= TryGetGuiTexture("b_gui_ig_i_ic_c_death");
            var cells = new List<Hud.TeamPortraits.Member>(_party.Count);
            for (int i = 0; i < _party.Count; i++)
            {
                var m = _party[i];
                if (m.PartyIndex == 0) continue;   // leader is slot 1 (character_awp)
                var c = m.Actor.Combat;
                var st = m.Actor.Stats;
                // Slot 1 = equipped melee, slot 2 = equipped ranged (from the
                // member's own template equipment); spells are slots 3/4 once
                // follower spellbooks are wired. Active = whichever they carry.
                // Read the member's LIVE equipment so gear equipped on their
                // paperdoll updates the strip (seeds from their template first).
                var mEquip = GetEquipmentDict(m.PartyIndex);
                var mSlot1 = ResolveWeaponSlotIcon(mEquip, "weapon_melee");
                var mSlot2 = ResolveWeaponSlotIcon(mEquip, "weapon_ranged");
                // Clicked slot wins; otherwise default to whichever weapon they carry.
                int mActive = _memberActiveSlot.TryGetValue(m.PartyIndex, out var ov)
                    ? ov : (mSlot1 is not null ? 0 : (mSlot2 is not null ? 1 : -1));
                // SC-COMPANION-SPELL — slot 3 shows the companion's active
                // spell (authored il_active_primary_spell or kit backfill).
                GlTexture? mSpellIcon = null;
                if (!string.IsNullOrWhiteSpace(m.Actor.Stats.PrimarySpell)
                    && _spellCatalog is not null
                    && _spellCatalog.TryGet(m.Actor.Stats.PrimarySpell!, out var mSp) && mSp is not null
                    && !string.IsNullOrWhiteSpace(mSp.ActiveIcon))
                    mSpellIcon = ResolveAwpSlotIcon(mSp.ActiveIcon);
                // SC-MEMBER-ACTIVE-SLOT — slot 4 = the member's Active Spell 2
                // straight from their spellbook (was hardwired null, so a
                // spell slotted into Active 2 never showed its icon).
                GlTexture? mSpell2Icon = null;
                var mBook = _memberSpellbooks.TryGetValue(m.PartyIndex, out var mbk)
                    ? mbk : BookFor(m.PartyIndex);
                if (mBook?.Secondary is { } mSec && !string.IsNullOrWhiteSpace(mSec.ActiveIcon))
                    mSpell2Icon = ResolveAwpSlotIcon(mSec.ActiveIcon);
                // SC-DOWNED — an unconscious member shows the red tint, not
                // the death mask (they're revivable, not a corpse).
                cells.Add(new Hud.TeamPortraits.Member(
                    ResolveMemberPortrait(m.Actor.Template),
                    st.MaxLife > 0f ? c.CurrentLife / st.MaxLife : 0f,
                    st.MaxMana > 0f ? c.CurrentMana / st.MaxMana : 0f,
                    m.IsDead || (c.IsDead && !c.Downed),
                    _selectedPartyIdx.Contains(m.PartyIndex),
                    mSlot1, mSlot2, mSpellIcon, mSpell2Icon, mActive,
                    Downed: c.Downed));
            }
            // SC-SUMMON-UI — the summon's cell: portrait + vitals, no
            // weapon/spell slots (it has no inventory), selectable so the
            // field commands and disband act on it.
            if (PlayerSummon is { } smn)
            {
                var sc = smn.Actor.Combat;
                var sst = smn.Actor.Stats;
                cells.Add(new Hud.TeamPortraits.Member(
                    ResolveMemberPortrait(smn.Actor.Template),
                    sst.MaxLife > 0f ? sc.CurrentLife / sst.MaxLife : 0f,
                    sst.MaxMana > 0f ? sc.CurrentMana / sst.MaxMana : 0f,
                    smn.IsDead || sc.IsDead,
                    _selectedPartyIdx.Contains(SummonSelIdx),
                    null, null, null, null, -1));
            }
            _teamPortraits.Draw(_iconRenderer, _barRenderer, viewportW, viewportH,
                                _awpAtlas, cells, _awpDeathTex, _awpInvBtnTex);
        }

        // Phase 27 — field_commands panel (bottom-right party orders). DS1 keeps
        // this cluster on screen from the moment the game starts — solo or not —
        // so it draws unconditionally, NOT gated on having followers (the
        // formation/order controls simply have no party to act on until one is
        // recruited).
        // SC-FC-ORDERS — the crystals/labels show the selected member's
        // standing orders (retail swaps the rollover text per selection the
        // same way); solo shows the DS1 defaults the first recruit will get.
        var fcDisp = FcDisplayMember();
        var fcState = new Hud.FieldCommandsPanel.State(
            FormationAction(),
            fcDisp?.FcMovement  ?? Hud.FieldCommandsPanel.Action.MoveEngage,
            fcDisp?.FcAttack    ?? Hud.FieldCommandsPanel.Action.AtkFightback,
            fcDisp?.FcTargeting ?? Hud.FieldCommandsPanel.Action.TgtClosest,
            FcFollowerDisplayMember()?.FcFollow ?? true,
            _fcCommandsCollapsed, _fcMinimized,
            ShowChat: _mpNetSession is not null);
        _fieldPanel.Draw(_barRenderer, _textRenderer, _iconRenderer,
                         TryGetGuiTexture, viewportW, viewportH, fcState);

        // SC-MP-PLAYERS — roster panel (MP only); SC-SP-CHAT — the chat
        // line + history draw in every session so Enter works solo too.
        if (_mpNetSession is not null && _mpPlayersPanel.IsOpen)
            _mpPlayersPanel.Draw(_barRenderer!, _textRenderer!, _iconRenderer,
                                 GetCommonTexture, TryGetGuiTexture,
                                 viewportW, viewportH, MpBuildRosterRows());
        DrawMpChat(viewportW, viewportH);
    }

    /// <summary>SC-MP-PLAYERS — chat lines above the bottom-left data bar +
    /// the entry line while typing (field-commands chat button / authored
    /// [toggle_gui_edit_box] Return binding).</summary>
    private void DrawMpChat(int vw, int vh)
    {
        if (_textRenderer is null || _barRenderer is null) return;
        int fs = Math.Max(1, (int)MathF.Round(HudScale.Hud(vh)));
        int lineH = 14 * fs;
        int baseY = vh - 96 * fs;
        var ink = new Vector4(0.92f, 0.90f, 0.80f, 1f);
        var shadow = new Vector4(0f, 0f, 0f, 0.85f);
        for (int i = 0; i < _mpChatLog.Count; i++)
        {
            int y = baseY - (_mpChatLog.Count - i) * lineH;
            var line = _mpChatLog[i].Line;
            _textRenderer.DrawString(vw, vh, line, 13, y + 1, shadow, fs);
            _textRenderer.DrawString(vw, vh, line, 12, y, ink, fs);
        }
        if (_mpChatOpen)
        {
            // Same authored dressing as the Save dialog's edit box: cpbox
            // chrome over a dark backing, parchment ink, blinking caret.
            var parch = new Vector4(0.88f, 0.82f, 0.70f, 1f);
            string label = "Say:";
            int labelW = _textRenderer.MeasureWidth(label, fs);
            int pad = 6 * fs;
            int boxH = lineH + 10;
            int boxW = Math.Max(_textRenderer.MeasureWidth(_mpChatBuffer, fs)
                                + labelW + pad * 3 + 10, 240 * fs);
            int bx = 8, by = baseY;
            _barRenderer.DrawRect(vw, vh, bx, by, boxW, boxH,
                                  new Vector4(0.07f, 0.07f, 0.08f, 0.85f));
            if (_iconRenderer is not null)
                Hud.NinePatch.DrawCpbox(_iconRenderer, GetCommonTexture, vw, vh,
                                        bx, by, boxW, boxH, Vector4.One);
            int ty = by + (boxH - _textRenderer.LineHeight * fs) / 2;
            _textRenderer.DrawString(vw, vh, label, bx + pad, ty,
                                     new Vector4(0.91f, 0.85f, 0.63f, 1f), fs);
            int tx = bx + pad * 2 + labelW;
            _textRenderer.DrawString(vw, vh, _mpChatBuffer, tx, ty, parch, fs);
            if (((int)(_playSeconds * 2)) % 2 == 0)
            {
                int caretX = tx + _textRenderer.MeasureWidth(_mpChatBuffer, fs) + 1;
                _barRenderer.DrawRect(vw, vh, caretX, ty, Math.Max(1, fs),
                                      _textRenderer.LineHeight * fs, parch);
            }
        }
    }

    /// <summary>Phase 22-H SC-HUD-OVERHEAD-BARS — DS1's authentic floating
    /// HP/MP bars above each actor's head. Source: status_bars.gas. The
    /// gas defines 8 health/mana bar pairs (one per character slot in MP)
    /// + enemy pair; engine selects which to draw per-actor. Bars use the
    /// shared b_gui_ig_mnu_status_bars atlas with per-bar uvcoords; this
    /// implementation projects each actor's worldPos to screen and draws
    /// the bar centered above the head.
    ///
    /// Visibility: player always shows; non-player combatants show when
    /// (a) brain is in Chase/Attack (aggro), or (b) they've taken damage
    /// in the last few seconds. Dead actors don't render.
    ///
    /// Reference: gas health_bar rect 209,286,265,294 (56×8 in 640×480
    /// space); mana_bar 172,282,228,289 (56×7). The engine offsets these
    /// rects per actor's screen position, ignoring the gas's literal X/Y.
    /// We scale by viewportH/480 and center horizontally on the actor.
    /// dynamic_edge=right: the bar fills from left as life ticks down, so
    /// we scale rect width and uvR by currentFrac (V-flip per the gas
    /// bottom-up convention same as data_bar.gas).</summary>
    // SC-HUD-OVERHEAD-BARS attribute-coverage block. Per
    // feedback_audit_asset_paths.md's 2026-05-13 fold, every gas attribute
    // either gets consumed by code OR a comment-justified skip. For
    // status_bars.gas elements:
    //   - alpha = 1.0          → Vector4.One default, no per-bar override
    //   - common_control = false → SiegeFX doesn't have a common-template
    //                             registry; bar style is hardcoded here
    //   - draw_order = 1/2     → implicit: HP drawn first, MP stacked below
    //   - rect (per-slot)      → IGNORED — gas authors slot-specific rects
    //                             but the engine offsets per actor's screen
    //                             position (we project worldPos to NDC and
    //                             center the 56×8 ref rect on it)
    //   - draw_outline = true  → DrawBorder call at end of DrawOverheadBar
    //   - texture              → b_gui_ig_mnu_status_bars (atlas, lazy)
    //   - uvcoords             → consumed per-bar with V-flip
    //   - wrap_mode = clamp    → texture loads with GL_REPEAT (GlTexture.cs:51)
    //                             — see U-clamp epsilon in DrawOverheadBar
    //                             which inset the right edge by 1 texel to
    //                             keep bilinear inside the strip even at
    //                             frac=1.0. SC-HUD-OVERHEAD-BARS-WRAP-PROPER
    //                             splinter: switch to ClampToEdge via a
    //                             per-GlTexture flag once we identify all
    //                             usages that need REPEAT (terrain tiles).
    //   - dynamic_edge = right → fillW + uClippedR scale by life fraction
    //   - border_color = 0xff000000 → black Vector4 passed to DrawBorder
    //   - pass_through = true  → click-routing hint for the gas widget
    //                             system; render-only path can ignore
    //   - [oncreated] setvisible(false) → DS1's "engine reveals per actor"
    //                             pattern; our visibility gate (X toggle +
    //                             non-controlled party members only) is the
    //                             equivalent at the rendering layer
    //   - The dim bg color (0.05/0.05/0.05 @ 0.82α) is SiegeFX-invented for
    //     readability of the empty region — NOT authored in DS1's gas
    //     (DS1 ships no bg layer; missing fill is just transparent).
    private void DrawOverheadStatusBars(int viewportW, int viewportH, Matrix4x4 viewProj)
    {
        if (!_overheadBarsVisible) return;
        if (_iconRenderer is null || _barRenderer is null) return;
        if (!_statusBarsLoaded)
        {
            _statusBarsLoaded = true;
            _statusBarsTexture = TryGetGuiTexture("b_gui_ig_mnu_status_bars");
            Console.WriteLine($"[overhead_bars] atlas: {(_statusBarsTexture is not null ? "ok" : "MISS")}");
        }
        if (_statusBarsTexture is null) return;

        // Gas-authored UV crops (bottom-up texture frame). Per the V-flip
        // rule documented in DrawDataBar, the actual screen-frame V used
        // in DrawIcon is computed as (1 - gasV1, 1 - gasV0).
        const float HpGasU0 = 0.000000f, HpGasV0 = 0.250000f, HpGasU1 = 0.843750f, HpGasV1 = 0.562500f;
        const float MpGasU0 = 0.000000f, MpGasV0 = 0.625000f, MpGasU1 = 0.843750f, MpGasV1 = 1.000000f;

        // Reference rect (640×480): health 56×8, mana 56×7. Stack mana
        // directly above health with a 1px gap, both centered horizontally
        // on the actor's projected head position. Size follows the shared
        // HUD baseline; position is the world-projected head (unaffected).
        float refScale = Hud.HudScale.Hud(viewportH);
        int barW = (int)Math.Round(56 * refScale);
        int hpH  = (int)Math.Round(8 * refScale);
        int mpH  = (int)Math.Round(7 * refScale);

        var dim    = new Vector4(0.05f, 0.05f, 0.05f, 0.82f);
        var border = new Vector4(0f, 0f, 0f, 1f);

        // Party members only, and only while the authored X toggle
        // ("Health/Mana Bars") is on. Enemies NEVER show overhead pools —
        // the always-on wounded/aggro reveal this shipped with was a
        // SiegeFX invention that read as HUD noise in playtests.
        for (int i = 0; i < _actors.Count; i++)
        {
            var s = _actors[i];
            if (s.IsDead) continue;
            var combat = s.Actor.Combat;
            var stats = s.Actor.Stats;
            if (stats.MaxLife <= 0f) continue;
            // INFORAIL fold — actively-controlled actor never shows
            // overhead bars; their HP/MP read off the AWP at top-left
            // (which is the canonical DS1 location for "the actor I'm
            // playing"). When SiegeFX gains party-control swapping,
            // IsPlayer tracks whichever actor the user currently drives,
            // so the bar follows the un-controlled party members.
            if (s.IsPlayer) continue;
            if (!s.IsPartyMember) continue;
            // Project worldPos + headOffset to screen via NDC.
            var headWorld = s.CurrentTransform.Translation + new Vector3(0f, 2.6f, 0f);
            var clip = Vector4.Transform(new Vector4(headWorld, 1f), viewProj);
            if (clip.W <= 0.01f) continue;
            float ndcX = clip.X / clip.W;
            float ndcY = clip.Y / clip.W;
            // Skip off-screen with a small margin so a bar mid-traversal
            // off the edge doesn't pop in/out at the seam.
            if (ndcX < -1.2f || ndcX > 1.2f || ndcY < -1.2f || ndcY > 1.2f) continue;
            int screenX = (int)Math.Round((ndcX + 1f) * 0.5f * viewportW);
            int screenY = (int)Math.Round((1f - ndcY) * 0.5f * viewportH);

            int barX = screenX - barW / 2;
            int hpY = screenY;
            int mpY = hpY + hpH + 1;
            float hpFrac = Math.Clamp(combat.CurrentLife / stats.MaxLife, 0f, 1f);
            DrawOverheadBar(viewportW, viewportH, barX, hpY, barW, hpH, hpFrac,
                            HpGasU0, HpGasV0, HpGasU1, HpGasV1, dim, border);
            // Mana bar only when the actor ships max mana > 0 (skips
            // chickens, props, mana-less brutes). Player always shows
            // the mana bar even at 0 because DS1 PCs always have some
            // mana from multiclass mechanics; defensive guard kept.
            if (stats.MaxMana > 0f)
            {
                float mpFrac = Math.Clamp(combat.CurrentMana / stats.MaxMana, 0f, 1f);
                DrawOverheadBar(viewportW, viewportH, barX, mpY, barW, mpH, mpFrac,
                                MpGasU0, MpGasV0, MpGasU1, MpGasV1, dim, border);
            }
        }
    }

    /// <summary>Render one overhead bar: dim bg full-width, textured fill
    /// scaled by life fraction, black 1px border. UV-V is flipped per the
    /// gas bottom-up convention (same rule the data_bar uses). UV-U is
    /// also clipped to the life fraction so the texture sample stays in
    /// register with the visible fill (DS1's dynamic_edge=right shrinks
    /// the bar from the right; we mirror by clipping U1 to U0 + frac*span).</summary>
    private void DrawOverheadBar(int viewportW, int viewportH,
        int x, int y, int w, int h, float frac,
        float gasU0, float gasV0, float gasU1, float gasV1,
        Vector4 dim, Vector4 border)
    {
        if (_barRenderer is null || _iconRenderer is null || _statusBarsTexture is null) return;
        // Background (always full width so the empty portion of the bar
        // reads as a hint of "you've lost this much").
        _barRenderer.DrawRect(viewportW, viewportH, x, y, w, h, dim);
        if (frac > 0f)
        {
            int fillW = (int)Math.Round(w * frac);
            if (fillW > 0)
            {
                // Clip the U axis to the fill fraction (gas U range × frac).
                // texelInset = 1 texel of safety on the right edge so a
                // bilinear sample at frac=1.0 doesn't pull in the next
                // atlas strip's leftmost texels — GlTexture loads with
                // GL_REPEAT (GlTexture.cs:51) instead of the gas-authored
                // wrap_mode=clamp; this epsilon paves over the mismatch
                // until SC-HUD-OVERHEAD-BARS-WRAP-PROPER lands. Texture
                // is ~128px wide; 1/256 ≈ half-texel keeps the inset
                // visually invisible (sub-pixel) while safely inside.
                const float texelInset = 1f / 256f;
                float uSpan = gasU1 - gasU0;
                float uClippedR = gasU0 + uSpan * frac - texelInset;
                if (uClippedR < gasU0 + texelInset) uClippedR = gasU0 + texelInset;
                _iconRenderer.DrawIcon(viewportW, viewportH, _statusBarsTexture,
                    x, y, fillW, h, Vector4.One,
                    gasU0, 1f - gasV1, uClippedR, 1f - gasV0);
            }
        }
        // 1px black outline (gas draw_outline=true + border_color=0xff000000).
        _barRenderer.DrawBorder(viewportW, viewportH, x, y, w, h, border);
    }

    /// <summary>SC-TOOLTIPS — the hover stat card for backpack items. Kept
    /// deliberately text-only (DS1's rollover_help chrome is a later polish);
    /// content is all authored data: screen_name, [attack] damage,
    /// [defend] defense, [magic][enchantments] description lines,
    /// [gui] equip_requirements (red when the wearer fails them),
    /// [aspect] gold_value.</summary>
    /// <summary>SC-TOOLTIP-LAYER — every hover stat card, drawn from ONE
    /// dispatcher at the very end of the HUD pass so no panel can paint
    /// over a card (companion tiles used to cover the player-bag card).
    /// Coverage: spellbook rows (player + companion tiles), backpack items
    /// (player + companion bags), and equipped paperdoll slots on whichever
    /// sheet is open — with spell pages showing the full spell card in every
    /// location.</summary>
    private void DrawHoverTooltips(int vw, int vh)
    {
        if (_textRenderer is null || _templateStore is null) return;
        if (_cursorItem is not null || _cursorScroll is not null) return;
        int mx = (int)_currentMousePos.X, my = (int)_currentMousePos.Y;

        // Spellbook rows — the retail spell stat card at the OWNER's skill.
        var hover = HoveredSpellbookRow(mx, my);
        if (hover is { } h && h.Spell is not null)
        {
            var caster = h.OwnerPidx <= 0 ? _player
                : _party.FirstOrDefault(p => p.PartyIndex == h.OwnerPidx);
            if (caster is not null)
            {
                DrawHoverCard(vw, vh, BuildSpellCardLines(h.Spell, caster));
                return;
            }
        }

        if (_inventoryOpen)
        {
            // Backpack items — main panel (active character), then the
            // tiled companion bags (same origin trick the click path uses).
            var inv = ActiveInventory;
            int ownerPidx = _paperdollTargetIndex;
            int idx = _inventoryPanel.TryHitTestItem(mx, my, vw, vh, inv, TryGetItemGridSize);
            if ((idx < 0 || idx >= inv.Count) && CompanionBagAt(mx, my) is { } bagHit)
            {
                inv = GetMemberInventory(bagHit.PartyIndex);
                ownerPidx = bagHit.PartyIndex;
                _companionInvPanel.OriginX = bagHit.OriginX;
                _companionInvPanel.OriginY = bagHit.OriginY;
                idx = _companionInvPanel.TryHitTestItem(mx, my, vw, vh, inv, TryGetItemGridSize);
            }
            if (idx >= 0 && idx < inv.Count)
            {
                DrawCardForItemRef(inv[idx].Reference, ownerPidx, vw, vh);
                return;
            }
        }

        // Equipped gear — the open sheet's paperdoll slots (player or
        // companion; ActiveEquipment already follows _paperdollTargetIndex).
        if (_charPanelOpen)
        {
            var slot = _paperdoll.TryHitTestSlot(mx, my, _paperdollRect.X, _paperdollRect.Y, vh);
            var esTag = slot is null ? null : PaperdollSlotToEsTag(slot);
            if (esTag is not null && ActiveEquipment.TryGetValue(esTag, out var eref)
                && !string.IsNullOrWhiteSpace(eref))
                DrawCardForItemRef(eref, _paperdollTargetIndex, vw, vh);
        }
    }

    /// <summary>Route one hovered item reference to the right card: spell
    /// pages get the full spell stat card (evaluated at the OWNING
    /// character's skill) wherever they sit; everything else gets the item
    /// card with requirements judged against the owner, not always the
    /// player.</summary>
    private void DrawCardForItemRef(string reference, int ownerPidx, int vw, int vh)
    {
        var owner = ownerPidx <= 0 ? _player
            : _party.FirstOrDefault(p => p.PartyIndex == ownerPidx);
        if (reference.StartsWith("spell_", StringComparison.OrdinalIgnoreCase)
            && _spellCatalog is not null && owner is not null
            && _spellCatalog.TryGet(ResolveItemRef(reference), out var sp) && sp is not null)
        {
            DrawHoverCard(vw, vh, BuildSpellCardLines(sp, owner));
            return;
        }
        var lines = BuildItemCardLines(ResolveItemRef(reference), owner);
        if (lines is not null) DrawHoverCard(vw, vh, lines);
    }

    /// <summary>SC-TOOLTIP-AUTH/SC-TOOLTIP-LAYER — the shared card plate:
    /// centered lines on the near-black backdrop, anchored beside the
    /// cursor and clamped on-screen. Callers run at the END of the HUD
    /// pass so the plate stacks above every panel.</summary>
    private void DrawHoverCard(int vw, int vh, List<(string Text, Vector4 Color)> lines)
    {
        if (_textRenderer is null || lines.Count == 0) return;
        int mx = (int)_currentMousePos.X, my = (int)_currentMousePos.Y;
        const int lineH = 13, pad = 10;
        int wpx = 0;
        foreach (var (t, _) in lines) wpx = Math.Max(wpx, _textRenderer.MeasureWidth(t, 1));
        wpx += pad * 2;
        int hpx = lines.Count * lineH + pad * 2;
        int x = Math.Clamp(mx + 20, 4, Math.Max(4, vw - wpx - 4));
        int y = Math.Clamp(my - hpx / 2, 4, Math.Max(4, vh - hpx - 4));
        _barRenderer?.DrawRect(vw, vh, x, y, wpx, hpx, new Vector4(0.02f, 0.02f, 0.02f, 0.94f));
        for (int li = 0; li < lines.Count; li++)
        {
            var (t, c) = lines[li];
            if (t.Length == 0) continue;
            int tw = _textRenderer.MeasureWidth(t, 1);
            _textRenderer.DrawString(vw, vh, t, x + (wpx - tw) / 2, y + pad + li * lineH, c, 1);
        }
    }

    /// <summary>SC-TOOLTIP-AUTH — the bottom-center help strip (retail
    /// screens 0026-0029): subject line over the education sentence, on the
    /// same translucent band language the top message strip uses.</summary>
    private void DrawBottomHelpStrip(int vw, int vh)
    {
        if (_textRenderer is null || _barRenderer is null) return;
        var help = ComputeBottomHelp(vw, vh);
        if (help is null) return;
        var (l1, c1, l2) = help.Value;
        const int lineH = 14;
        int linesN = l2 is null ? 1 : 2;
        int bandH = linesN * lineH + 8;
        int bandW = (int)(vw * 0.62f);
        int bandX = (vw - bandW) / 2;
        int bandY = vh - bandH - 4;
        _barRenderer.DrawRect(vw, vh, bandX, bandY, bandW, bandH,
            new Vector4(0f, 0f, 0f, 0.55f));
        int w1 = _textRenderer.MeasureWidth(l1, 1);
        _textRenderer.DrawString(vw, vh, l1, (vw - w1) / 2, bandY + 4, c1, 1);
        if (l2 is not null)
        {
            int w2 = _textRenderer.MeasureWidth(l2, 1);
            _textRenderer.DrawString(vw, vh, l2, (vw - w2) / 2, bandY + 4 + lineH,
                new Vector4(0.95f, 0.93f, 0.85f, 1f), 1);
        }
    }

    /// <summary>Phase 20c — render a chevron pointing at the active quest's
    /// nearest live target. Two cases: on-screen target gets a chevron drawn at
    /// the projected screen position (above the actor's head); off-screen or
    /// behind-camera target gets the chevron clamped to a 28-px margin around
    /// the viewport, rotated to point outward. Draws nothing when no active
    /// quest has a kill objective or no live target matches.</summary>
    private void DrawQuestGoalMarker(int viewportW, int viewportH, Matrix4x4 vp)
    {
        if (_progression is null || _barRenderer is null) return;

        // Pick the first active quest with an unmet kill objective. Stable
        // pick (enumeration order in the Dictionary is insertion order in
        // .NET's implementation) so the marker doesn't strobe between quests.
        SiegeFX.Core.Actors.QuestEntry? questEntry = null;
        foreach (var e in _progression.Journal.Active)
        {
            if (e.Definition is null) continue;
            if (e.Definition.KillCountGoal <= 0) continue;
            if (e.KillProgress >= e.Definition.KillCountGoal) continue;
            questEntry = e; break;
        }
        if (questEntry?.Definition is not { } qdef) return;

        // Find nearest live actor whose template matches the goal substring.
        var anchor = _player?.CurrentTransform.Translation ?? _camera.Position;
        ActorRenderState? best = null;
        float bestDist = float.MaxValue;
        foreach (var s in _actors)
        {
            if (s.IsDead || s.Actor.Combat.IsDead) continue;
            if (s.Actor.Template.Name.IndexOf(qdef.KillTargetTemplate,
                StringComparison.OrdinalIgnoreCase) < 0) continue;
            float d = Vector3.DistanceSquared(s.CurrentTransform.Translation, anchor);
            if (d < bestDist) { bestDist = d; best = s; }
        }
        if (best is null) return;

        // Project the target's head to clip space. Behind the camera handling:
        // we still want to show an off-screen arrow pointing toward it, so when
        // W<=0 we flip the projected NDC by negating both axes — that yields an
        // off-screen point in roughly the right direction once clamped.
        var headPos = best.CurrentTransform.Translation + new Vector3(0f, 2.4f, 0f);
        var clip    = Vector4.Transform(new Vector4(headPos, 1f), vp);
        bool behind = clip.W <= 0.001f;
        float ndcX  = clip.X / (behind ? -clip.W : clip.W);
        float ndcY  = clip.Y / (behind ? -clip.W : clip.W);
        if (behind) { ndcX = -ndcX; ndcY = -ndcY; }

        float sx = (ndcX * 0.5f + 0.5f) * viewportW;
        float sy = (1f - (ndcY * 0.5f + 0.5f)) * viewportH;

        const int margin = 28;
        bool offscreen = behind || sx < margin || sx > viewportW - margin
                                || sy < margin || sy > viewportH - margin;

        var marker = new Vector4(1.00f, 0.85f, 0.40f, 0.95f);
        if (!offscreen)
        {
            // On-screen: chevron above the actor's head — small downward
            // triangle (3 stacked rects) so we don't need a triangle batcher.
            int cx = (int)sx, cy = (int)sy;
            _barRenderer.DrawRect(viewportW, viewportH, cx - 7, cy - 14, 14, 3, marker);
            _barRenderer.DrawRect(viewportW, viewportH, cx - 4, cy - 10, 8,  3, marker);
            _barRenderer.DrawRect(viewportW, viewportH, cx - 1, cy -  6, 2,  3, marker);
        }
        else
        {
            // Off-screen: clamp to a margin rectangle around the viewport so
            // the player can still see "the quest target is over there".
            float cxF = MathF.Min(MathF.Max(sx, margin), viewportW - margin);
            float cyF = MathF.Min(MathF.Max(sy, margin), viewportH - margin);
            int cx = (int)cxF, cy = (int)cyF;
            // 12px square pip with a 4px tail toward the off-screen direction.
            _barRenderer.DrawRect(viewportW, viewportH, cx - 6, cy - 6, 12, 12, marker);
            float dx = sx - viewportW * 0.5f;
            float dy = sy - viewportH * 0.5f;
            float len = MathF.Sqrt(dx * dx + dy * dy);
            if (len > 0.001f)
            {
                int tx = cx + (int)(dx / len * 10f);
                int ty = cy + (int)(dy / len * 10f);
                _barRenderer.DrawRect(viewportW, viewportH, tx - 2, ty - 2, 4, 4, marker);
            }
        }
    }

    private void OnRender(double dt)
    {
        if (_gl is null) return;
        _frameStamp++; // ALPHA-PERF — gates once-per-frame uniform uploads
        if (_diagMode) DiagRecordFrame(dt);
        ReconcileTradeInventory();
        if (_saveToastRemaining > 0f) _saveToastRemaining -= (float)dt;
        // SC-MSG-STRIP / SC-CHAPTER-TITLE — tick + prune the message lines
        // and the chapter card.
        // SC-KEY-AUDIT — ScrollLock pins the strip: no expiry while locked.
        if (!_gameMsgLocked)
            for (int i = _gameMessages.Count - 1; i >= 0; i--)
            {
                _gameMessages[i].Remaining -= (float)dt;
                if (_gameMessages[i].Remaining <= 0f) _gameMessages.RemoveAt(i);
            }
        if (_chapterTitleRemaining > 0f) _chapterTitleRemaining -= (float)dt;
        if (_levelUpFxRemaining > 0f) _levelUpFxRemaining -= (float)dt;
        // Phase 18c — listener follows the PC every frame. We use camera
        // forward (not _playerFacing) so the audio image rotates with
        // the camera instead of the body — matches what the user is
        // looking at and avoids weird pans when the PC stands still
        // facing one way while the user orbits the cam. Falls back to
        // camera position when no PC has spawned (debug fly-cam mode).
        if (_audio is not null)
        {
            var listenerPos = _player?.CurrentTransform.Translation ?? _camera.Position;
            _audio.UpdateListener(listenerPos, _camera.Forward, Vector3.UnitY);
        }
        // Phase 17a — tick + prune floating cast feedback. Reverse-iterate so
        // RemoveAt is O(1) per kill and the list compacts in-place.
        for (int i = _floatingTexts.Count - 1; i >= 0; i--)
        {
            _floatingTexts[i].Remaining -= (float)dt;
            if (_floatingTexts[i].Remaining <= 0f) _floatingTexts.RemoveAt(i);
        }
        // Phase 22-A SC-HUD-DATABAR pause gate (OnRender side) — music ticks
        // use real dt (on pause it fades out over a few seconds, as the
        // original's does). World ticks (frag debris, particles, weather) use
        // `simDt`, which is zero when paused: pausing the original freezes
        // the rain, torch flames and spell effects in place.
        float simDt = SimFrozen ? 0f : (float)(dt * _gameSpeed);
        // Phase 17-SC-E — integrate billboard particles + lightning bolts +
        // (Phase 21-SC-SPELL-VFX) flying projectile heads. The screen-space
        // _spellBolts trail was retired here; primary cast visuals are now
        // the 3D world-space primitives spawned through SpawnSpellVisual.
        // SC-TORCH-FLAME — burn every captured torch/candlestand socket. A
        // warm plume before the particle integrate step so this frame's spawn
        // advances with the rest. Emit only when reasonably near the camera so
        // a region full of sconces doesn't flood the particle budget.
        if (_particles is not null && _flameSources.Count > 0)
        {
            // Original torch flame (the size/shape before the look tuning).
            // MaintainTorchFlame is kept in the particle system as the seed
            // for the future opt-in enhanced-effects layer, but the shipped
            // default is this plain plume.
            var flameCol = s_flameTint;
            var camPos = _camera.Position;
            foreach (var f in _flameSources)
            {
                if (Vector3.DistanceSquared(f.Pos, camPos) > 60f * 60f) continue;
                f.Carry = _particles.MaintainFire(f.Pos, flameCol, 0.22f, simDt, 18f, f.Carry);
            }
        }
        // SC-WEATHER-E — pump mood precipitation in a disc over the player.
        // Game time, so the rain stops in mid-air when the game is paused, as
        // the original's does. Rates come pre-drifted from WeatherSystem; wind shears the
        // fall vectors. Floor = the player's ground Y — DS1 rain draws in a
        // cylinder around the camera focus without geometry collision.
        if (_particles is not null && (_weather.RainRate > 0.5f || _weather.SnowRate > 0.5f))
        {
            var wxFocus = _player?.CurrentTransform.Translation ?? _camera.Position;
            float wxFloor = _player is not null ? wxFocus.Y : wxFocus.Y - 8f;
            _particles.MaintainWeather(simDt, _weather.RainRate, _weather.SnowRate,
                _weather.WindVector, wxFocus, wxFloor);
        }
        _particles?.Tick(simDt);
        // Phase 22-SC-MUSIC-A/B — refill the music streaming queue every
        // frame. Cheap when nothing's playing (one OpenAL state read);
        // when a track is active it decodes ~0-3 chunks per tick to
        // refill drained buffers.
        // Phase 22-SC-MUSIC-FOLD — Tick returns false on natural EOS
        // (decoder ran out, source drained). Clear _currentMusicTrack
        // so a subsequent PlayMusicTrack with the same basename
        // actually re-fires — without this, a frontend track that
        // played to completion would refuse to replay because the
        // idempotency guard still thought it was active.
        if (_music is not null && !_music.Tick() && _currentMusicTrack.Length > 0)
            _currentMusicTrack = "";
        TickMusicPauseFade((float)dt);
        // Phase 22-SC-MUSIC-D — flip music to mood.battle_track when any
        // hostile NPC is engaging the player; revert to standard_track
        // after CombatExitDelay seconds without aggro. Cheap loop over
        // _actors; bails on the first hostile so worst-case is
        // population-bounded but typically O(few).
        TickCombatMusic((float)dt);
        // SC-WEATHER-C — apply pending mood_change requests + advance the
        // weather state machine (fog/rain/snow/wind lerps, drift, lightning
        // schedule, thunder cues). Game time: the original's weather stops
        // while paused (rain, drift and lightning alike).
        TickWeather(simDt);
        // SC-WEATHER-F — placed sound emitters (loops track Active state,
        // repeat windows count down, one-shots fire on activation edges).
        TickSoundEmitters((float)dt);
        // Phase 21-SC-BARREL-C — integrate frag debris (gravity + ground
        // settle). Same per-tick cadence as particles; a frag's settled
        // pose then lasts until lifetime expires.
        TickFragDebris(simDt);
        TickDoors(simDt);
        TickElevators(simDt);
        // SC-MOB-ROAM-AUDIT — 15s roam heartbeat to the session log:
        // brain-state counts + any actor that neither moved nor died since
        // the last beat while path-blocked. One line per beat; the headless
        // twin is `siegefx region roam-sim` (test-all #113).
        TickRoamAudit((float)dt);
        // SC-REGION-LAYER-HIDE — sticky underground/surface mode.
        // While underground, all upper-layer terrain / props / actors
        // are dropped from render (matches DS1 — upper world is just
        // black when you're in a basement/cave/dungeon).
        UpdateUndergroundMode();
        // SC-CAMERA-FADE — hide every camera_fade=true snode whose
        // world AABB sits between the camera eye and the player.
        // Cheap (only camera_fade-flagged snodes evaluate, ~5-10 in
        // a typical region scope), binary in/out, no per-frame
        // allocation. Drives both the render whole-snode skip and
        // the nav fade-hidden mask so click-pick falls through to
        // the basement floor while the upper structure is hidden.
        UpdateCameraFade();
        // Phase 17-SC-F-2 — advance any active sfx_script coroutines and
        // run continuous-emitter spawn budgets. Must run after the particle
        // Tick so this frame's spawns get drawn rather than waiting one tick.
        _sfxRuntime?.Tick((float)dt);
        // Phase 9-SC-9 — advance toss arcs on freshly-dropped piles. Linear
        // XZ lerp from feet to target with a parabolic Y arc (0 → ArcHeight
        // at midpoint → 0 at landing). Once Elapsed reaches Duration the
        // throw is cleared so auto-pickup becomes eligible.
        for (int i = 0; i < _lootPiles.Count; i++)
        {
            var pile = _lootPiles[i];
            var th = pile.Throw;
            if (th is null) continue;
            th.Elapsed += (float)dt;
            float t = th.Elapsed / th.Duration;
            if (t >= 1f)
            {
                pile.Position = th.Target;
                pile.RotationY = 0f;
                pile.RotationX = 0f;
                pile.Throw = null;
                // SC-AUDIO-ITEMS — DS1 plays the item's put_down at LANDING,
                // not at the toss. Every airborne pile funnels through here.
                PlayPileLandSfx(pile);
                continue;
            }
            var basePos = Vector3.Lerp(th.Source, th.Target, t);
            float arc = th.ArcHeight * 4f * t * (1f - t); // peak at t=0.5
            pile.Position = new Vector3(basePos.X, basePos.Y + arc, basePos.Z);
            pile.RotationY = th.StartRotation + th.Spins * MathF.PI * 2f * t;
            // Phase 21-SC-SCROLL-F — X-axis tumble. Same easing as Y: linear
            // through the flight, comes to rest exactly on landing because
            // the t>=1 branch above clears it. With XSpins=1.0 the item
            // does one full end-over-end during the throw arc; legacy
            // enemy drops keep XSpins=0 for the original pure-yaw look.
            pile.RotationX = th.StartRotationX + th.XSpins * MathF.PI * 2f * t;
        }
        // Phase 21-SC-SCROLL-GLITTER — DS1's continuous "pixie dust" on
        // resting spell scrolls. Distinct visual recipe per user feedback:
        // pure white (not element-tinted), pronounced, and SCATTERED
        // across the scroll's top surface with a rolling drift — like
        // twinkling stars rolling across a night sky.
        //
        // Per-pile timer accumulates spawn budget at glitterRate; when
        // it tips over an integer, fires SpawnTwinkle which scatters the
        // batch across the pile's footprint (uniform-area XZ sample) and
        // gives each particle a tangential drift velocity so the field
        // visibly moves rather than puffing in place. Y-offset puts the
        // emit plane just above the scroll mesh's top (pile renders at
        // pos.Y + pileSize=0.5, so 0.55 floats sparkles right above the
        // visible scroll surface — the prior 0.10 emitted UNDER the mesh
        // which read as "wrong side of the scroll").
        //
        // Pickup flow already removes scroll items from the pile, so the
        // glitter naturally turns off when the player picks up — no
        // explicit gate needed.
        if (_particles is not null)
        {
            // Tuning per "storm cloud" user feedback (b4accc5 v2 was too
            // tall + too loose). Hugging the scroll mesh now: emit plane
            // just above the visible top, half the prior XZ scatter, and
            // shorter lifetime so particles can't accumulate altitude
            // before they fade.
            const float glitterRate = 18f;          // sparkles per second per pile
            const float scrollTopY  = 0.30f;        // was 0.55 — emit just above the resting scroll
            const float footprintR  = 0.16f;        // was 0.30 — tighter cluster on the scroll
            // SC-WORLD-INVENTORY-VIEW-DISTANCE — match the render cull via the
            // shared WorldInventoryVisRadius constant so the two paths can't
            // drift. Far-away world-inventory scrolls would otherwise burn
            // CPU emitting invisible sparkles every frame.
            float glitterR2 = WorldInventoryVisRadius * WorldInventoryVisRadius;
            Vector3 glitterPlayerPos = _player?.CurrentTransform.Translation ?? Vector3.Zero;
            bool glitterHavePlayer = _player is not null;
            for (int i = 0; i < _lootPiles.Count; i++)
            {
                var pile = _lootPiles[i];
                if (pile.Throw is not null) continue;
                if (pile.IsWorldInventory && glitterHavePlayer)
                {
                    float pdx = pile.Position.X - glitterPlayerPos.X;
                    float pdz = pile.Position.Z - glitterPlayerPos.Z;
                    if (pdx * pdx + pdz * pdz > glitterR2) { pile.GlitterCarry = 0f; continue; }
                }
                string? scrollRef = null;
                foreach (var entry in pile.Items)
                {
                    if (string.IsNullOrEmpty(entry.Slot)
                        && entry.Reference.StartsWith("spell_", StringComparison.OrdinalIgnoreCase))
                    {
                        scrollRef = entry.Reference;
                        break;
                    }
                }
                if (scrollRef is null) { pile.GlitterCarry = 0f; continue; }
                pile.GlitterCarry += glitterRate * (float)dt;
                if (pile.GlitterCarry < 1f) continue;
                int batch = (int)pile.GlitterCarry;
                pile.GlitterCarry -= batch;
                _particles.SpawnTwinkle(
                    pile.Position + new Vector3(0f, scrollTopY, 0f),
                    SpellSparkleColor(scrollRef),
                    footprintRadius: footprintR,
                    scale: 0.14f,         // was 0.18 — tighter visual size
                    duration: 0.45f,      // was 0.70 — fade before drifting too high
                    count: batch);
            }
        }
        // ALPHA-2 POINT LIGHTS — select this frame's nearest sources once;
        // every ApplyLightingUniforms call this frame uploads the same set.
        PickFramePointLights();
        // SC-WEATHER-D — with mood fog active, the void beyond loaded nodes
        // clears to the fog color (retail's linear fog fades the world edge
        // into the same tone, so unloaded space must match or the horizon
        // shows a hard silhouette line). Flash folded in so lightning also
        // lights the sky, not just geometry. No fog → the original dark clear.
        if (_weather.FogActive && _weather.FogFar > _weather.FogNear && !_megaMapActive)
        {
            // SC-MEGAMAP-NO-FOG — geometry renders unfogged under the TAB
            // overview, so the void keeps the dark clear there too (a fog-
            // bright backdrop against unfogged terrain read wrong).
            var cc = FogColorWithFlash();
            _gl.ClearColor(cc.X, cc.Y, cc.Z, 1f);
        }
        else
        {
            _gl.ClearColor(0.08f, 0.09f, 0.11f, 1f);
        }
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var size = _window.FramebufferSize;

        // Phase 24-MAINMENU step 1+2 — boot mode renders the splash → main
        // menu sequence in the absence of a loaded region. Runs early so
        // the gameplay-render block below (which checks for null tanks /
        // null player and silently skips) sees a black-cleared frame; the
        // splash overlay is drawn by DrawBootScene at the HUD-pass site.
        // Phase 24-POLISH-A — early-return so the dev fly-cam grid + any
        // other always-on debug helpers don't peek through behind the
        // splash / main menu. Only the boot-scene draw runs in boot mode.
        if (_bootMode)
        {
            DrawBootScene((float)dt, size.X, size.Y);
            return; // Silk.NET swaps buffers automatically when OnRender returns
        }
        // SC-HERO-PORTRAIT — re-render the live head-shot before the world
        // pass whenever appearance changed (spawn, load, variant, gear).
        if (_portraitRefreshAt > 0 && _playSeconds >= _portraitRefreshAt)
        {
            _portraitRefreshAt = -1;
            _portraitDirty = true;
        }
        if (_portraitDirty && _player is not null && _skinShader is not null)
        {
            _portraitDirty = false;
            try { RenderPlayerPortrait(); }
            catch (Exception ex) { Console.WriteLine($"[portrait] render failed: {ex.Message}"); }
        }
        var aspect = size.Y == 0 ? 1f : (float)size.X / size.Y;
        var vp = _camera.GetViewProjection(aspect);

        // SC-TERRAIN-WHITE-GRID (audit fold — finding #3) — dev
        // fly-cam reference grid. Previously gated on
        // `_cameraMode == CameraMode.Fly` but that default-Fly value
        // ALSO covers the boot/main-menu / pre-PC-load window where
        // the world hasn't been built yet — the grid would draw
        // through any UI background. Tighter gate: require BOTH the
        // Fly cam AND a player to have been spawned (means we're in
        // play-region with the user actively flying around) AND the
        // explicit dev-mode toggle. For gameplay the grid is OFF; in
        // mesh-viewer / anim-viewer modes (no _player) the grid is
        // also OFF, which is what we want.
        if (_gridShader is not null && _grid is not null
            && _cameraMode == CameraMode.Fly && _player is not null)
        {
            _gridShader.Use();
            _gridShader.SetMatrix4("uViewProj", vp);
            _grid.Draw();
        }

        if (_meshShader is not null && _mesh is not null)
        {
            _meshShader.Use();
            _meshShader.SetMatrix4("uViewProj", vp);
            _meshShader.SetMatrix4("uModel", Matrix4x4.Identity);
            _meshShader.SetInt("uAlbedo", 0);
            _meshShader.SetInt("uHasTexture", _texture is null ? 0 : 1);
            _meshShader.SetInt("uFlipV", 1);
            ApplyLightingUniforms(_meshShader);
            _texture?.Bind(TextureUnit.Texture0);
            _mesh.Draw();
        }

        if (_meshShader is not null && _sno is not null)
        {
            _meshShader.Use();
            _meshShader.SetMatrix4("uViewProj", vp);
            _meshShader.SetMatrix4("uModel", Matrix4x4.Identity);
            _meshShader.SetInt("uAlbedo", 0);
            _meshShader.SetInt("uFlipV", 1);
            _meshShader.SetInt("uUvOrient", _terrainUvOrient);
            ApplyLightingUniforms(_meshShader);
            _meshShader.SetInt("uUseBakedLight", 1); // ALPHA-2 RADIOSITY
            _meshShader.SetInt("uAlbedo2", 1);
            for (var i = 0; i < _sno.Subsets.Count; i++)
            {
                var subset = _sno.Subsets[i];
                ApplyAnimatedTextureBinding(subset.TextureName);
                _sno.DrawSubset(i);
            }
            ResetAnimatedTextureBinding();
            _meshShader.SetInt("uUvOrient", 0);
        }

        if (_skinShader is not null && _skinnedMesh is not null && _skinnedAsp is not null && _anim is not null)
        {
            // Loop the clip by wrapping the running time. AnimationRuntime clamps internally,
            // so this is just for visual continuity — without the wrap a long-running session
            // would freeze on the last keyframe forever.
            var t = (float)(_anim.AnimLength > 0f ? _animTime % _anim.AnimLength : 0.0);
            var skin = AnimationRuntime.ComputeSkinMatrices(_skinnedAsp, _anim, t);

            _skinShader.Use();
            _skinShader.SetMatrix4("uViewProj", vp);
            _skinShader.SetMatrix4("uModel", Matrix4x4.Identity);
            // "uBones[0]" not "uBones": GL 3.3 spec says either is valid for array-of-basic-type
            // uniforms, but several older Intel / mesa drivers only return a real location for
            // the element-0 form. With the bare name, SetMatrix4Array silently no-ops (loc<0)
            // and you get a T-posed mesh with no GL error.
            _skinShader.SetMatrix4Array("uBones[0]", skin);
            _skinShader.SetInt("uAlbedo", 0);
            _skinShader.SetInt("uHasTexture", _animTexture is null ? 0 : 1);
            _skinShader.SetInt("uFlipV", 1);
            ApplyLightingUniforms(_skinShader);
            _animTexture?.Bind(TextureUnit.Texture0);
            _skinnedMesh.Draw();
        }

        if (_skinShader is not null && _actors.Count > 0)
        {
            // One draw call per actor. The mesh cache keeps unique ASPs down (DS1 ships ~12
            // distinct archetypes per region) but each instance has its own bones, so we
            // re-upload uBones per actor. This is cheap at 181 actors — can batch per-mesh
            // later if a 4-region stream pushes the actor count past ~2k.
            _skinShader.Use();
            _skinShader.SetMatrix4("uViewProj", vp);
            _skinShader.SetInt("uAlbedo", 0);
            ApplyLightingUniforms(_skinShader);
            // Phase 21c-4 — uFlipV is now per-actor (set inside the loop) because
            // older ASP versions (v2.3, krug et al) author UVs bottom-up to match
            // .raw byte order while v2.5+ (player farmboy, late-content NPCs) use
            // D3D top-down. One global flip got either the krug or the player
            // wrong. See SetFlipForMesh below.
            // Phase 21c — track the currently-bound albedo so we don't rebind for runs
            // of actors sharing the same mesh (DS1 regions ship ~12 unique archetypes
            // for ~180 actors, so most adjacent draws hit the cache). Phase 21c-4 keys
            // on (template, mesh) because krug_grunt and krug_scout share the same
            // pose mesh but each template overrides the texset name.
            int lastFlipV = -1;
            // Phase 21d-2a-v — uFlipV is the same for ALL skinned actors. Earlier
            // analysis (Phase 21c-4) thought v2.5+ ASPs needed the flip. The
            // subset-tint diagnostic in 21d-2a-v showed otherwise: farmboy's head
            // subset (asp V[0.01,0.54]) needs to sample the face/hair region of
            // its skin .raw, which sits at the start of file bytes (= GL V≈0)
            // because OpenGL puts byte 0 at the bottom-left and ImageSharp dumps
            // confirm face data is at PNG row 0. With uFlipV=1 those UVs invert
            // to GL V[0.46,0.99] = top of GL = the brown gradient strip, which
            // is exactly the "smeared face / no detail" the user reported.
            // SIEGEFX_FORCE_FLIPV=0|1 overrides for one-off testing.
            int defaultFlipV = 0;
            string? forceFlipEnv = Environment.GetEnvironmentVariable("SIEGEFX_FORCE_FLIPV");
            if (forceFlipEnv == "0") defaultFlipV = 0;
            else if (forceFlipEnv == "1") defaultFlipV = 1;
            foreach (var s in _actors)
            {
                // SC-REGION-LAYER-HIDE-ACTORS (follow-up) — actors carry no
                // region stamp yet, so the region-layer gate can't apply;
                // the IsPosInFadedSnode gate below covers actors standing on
                // fade-hidden surface terrain, which is the common overhead case.
                // SC-FADE-GROUPS — actors standing in a faded-out layer
                // (the farmhouse surface while the party is in the cellar)
                // hide with their terrain. NEVER the player (the cutaway
                // exists to show them), and NPCs test their LIVE transform —
                // Actor.WorldTransform is the authored spawn pose, which for
                // a moving actor points at wherever it spawned (the player's
                // spawn is on the surface → body culled with the surface
                // while the separately-drawn boots + dagger kept walking).
                if (!s.IsPlayer && IsPosInFadedSnode(s.CurrentTransform.Translation)) continue;
                // Intro NIS gizmos (narrator voice-over; spent sleeping dog) stay in
                // _actors for scripting but never draw as visible NPCs.
                if (s.IsPlayer && s.Hidden)
                    BodyDiag("player-hidden", "player render state Hidden=TRUE — body draw SKIPPED");
                if (s.Hidden) continue;
                if (s.IsPlayer)
                {
                    var dp = s.CurrentTransform.Translation;
                    BodyDiag("draw-player",
                        $"drawing player body: mesh={s.Actor.Mesh.MeshName} " +
                        $"subsets={s.Actor.Mesh.Subsets.Length} bones={s.Actor.Mesh.BoneCount} " +
                        $"clips={s.Actor.Clips.Length} scale={s.Actor.Stats.RenderScale:F2} " +
                        $"pos=({dp.X:F1},{dp.Y:F1},{dp.Z:F1})");
                }
                int flipV = defaultFlipV;
                if (flipV != lastFlipV)
                {
                    _skinShader.SetInt("uFlipV", flipV);
                    lastFlipV = flipV;
                }
                var clips = s.Actor.Clips;
                int boneCount = s.Actor.Mesh.BoneCount;
                ReadOnlySpan<Matrix4x4> skin;
                if (clips.Length == 0)
                {
                    // No parsable PRS for this actor (shipped 0x0202 clips we don't support
                    // yet). Identity per bone = bind-pose pass-through, which renders the
                    // mesh as authored. Good enough to confirm placement. The cached
                    // identity array is reused across frames per mesh, so no per-frame
                    // alloc here either.
                    if (!_actorIdentityBones.TryGetValue(s.Actor.Mesh, out var identity))
                    {
                        identity = new Matrix4x4[boneCount];
                        for (int i = 0; i < identity.Length; i++) identity[i] = Matrix4x4.Identity;
                        _actorIdentityBones[s.Actor.Mesh] = identity;
                    }
                    skin = identity;
                }
                else
                {
                    // Phase 21c-4 — pick walk clip (Actor.WalkClipIndex) when the brain
                    // is translating this actor; otherwise honor the skrit-driven default.
                    // Without this every NPC played its idle while striding across the map.
                    int idx;
                    // A pinned chore override (fall/dead/look, cast, swing) wins over the
                    // IsMoving walk swap — matches the AnimTime tick's gate. Without the
                    // !IsOverrideActive check a scripted actor that stopped moving with a
                    // held pose (Norick's "fall") kept drawing the walk clip in place.
                    if (!s.IsDead && s.IsMoving && !s.Actor.Host.IsOverrideActive
                        && s.Actor.WalkClipIndex >= 0 && s.Actor.WalkClipIndex < clips.Length)
                        idx = s.Actor.WalkClipIndex;
                    else
                        idx = Math.Min(s.Actor.CurrentClipIndex, clips.Length - 1);
                    var clip = clips[idx];
                    // Phase 12-SC-4 — dead actors hold the last frame of chore_die
                    // (clamp to AnimLength) instead of looping. Without the clamp the
                    // corpse would replay the death over and over.
                    float t;
                    if (clip.AnimLength <= 0f) t = 0f;
                    else if (s.IsDead) t = MathF.Min((float)s.AnimTime, clip.AnimLength - 0.01f);
                    else t = (float)(s.AnimTime % clip.AnimLength);
                    // Phase 21b-2 — write into the shared scratch buffer instead of
                    // allocating a fresh Matrix4x4[] per actor per frame. The upload
                    // below copies the bytes synchronously so the next iteration may
                    // overwrite the buffer immediately.
                    if (_skinScratch.Length < boneCount)
                        _skinScratch = new Matrix4x4[Math.Max(boneCount, 64)];
                    AnimationRuntime.ComputeSkinMatrices(s.Actor.Mesh, clip, t, _skinScratch);
                    skin = _skinScratch.AsSpan(0, boneCount);
                }
                // SC-ACTOR-SCALE — apply the creature's authored [aspect] scale_base
                // (× scale_multiplier) to the visual model only. Most mobs are 1.0
                // (no-op); shrink-scaled ones (phrak 0.55, skrubbs 0.3-0.5) render at
                // native size without this, oversized and clipping through the ground.
                float renderScale = s.Actor.Stats.RenderScale;
                _skinShader.SetMatrix4("uModel",
                    renderScale == 1f ? s.CurrentTransform
                                      : Matrix4x4.CreateScale(renderScale) * s.CurrentTransform);
                _skinShader.SetMatrix4Array("uBones[0]", skin);

                // Phase 21d-2a-ii — walk per-mesh subsets emitted by the ASP parser.
                // Each subset is one BSMM (textureIndex, faceSpan) record carved out
                // of the flattened triangle list; binding per subset prevents farmboy's
                // clothing strip (slot 1 = b_c_pos_a1_015) from inheriting the skin
                // sampler bound for slot 0. Single-subset meshes (krug, goblins, most
                // monsters) issue one bind + one DrawSubset, identical in cost to the
                // old single Draw() path.
                var subsets = s.Actor.Mesh.Subsets;
                if (subsets.Length == 0)
                {
                    BindActorSlot(s.Actor, 0);
                    s.GlMesh.Draw();
                }
                else
                {
                    int lastSlot = -1;
                    int subsetIdx = 0;
                    foreach (var sub in subsets)
                    {
                        if (sub.TextureIndex != lastSlot)
                        {
                            BindActorSlot(s.Actor, sub.TextureIndex);
                            lastSlot = sub.TextureIndex;
                        }
                        if (_subsetTintActive)
                        {
                            // Phase 21d-2a-v — solid-color tint per subset (red, green,
                            // blue, yellow, magenta, cyan, orange, purple). Wraps if a
                            // mesh has more subsets than palette entries; the largest
                            // shipped DS1 multi-subset mesh tops out at 5 (farmboy body),
                            // well under the palette length.
                            var (tr, tg, tb) = SubsetTintFor(subsetIdx);
                            _skinShader.SetInt("uSubsetTintActive", 1);
                            _skinShader.SetVec4("uSubsetTint", tr, tg, tb, 1f);
                        }
                        s.GlMesh.DrawSubset(sub.FirstTriangle, sub.TriangleCount);
                        subsetIdx++;
                    }
                    if (_subsetTintActive)
                        _skinShader.SetInt("uSubsetTintActive", 0);
                }
            }
            // SC-ACTOR-TSD — never let a lava-spirit layer-2 binding leak into the
            // equipment/weapon passes below (they bind Texture0 only and would
            // otherwise composite the scrolling molten layer over armour).
            if (_skinLayer2Active) { ResetAnimatedTextureBinding(_skinShader); _skinLayer2Active = false; }

            // SC-PROP-ANIM — animated scenery pass. Same skin-shader state as
            // the actor loop above; each prop derives its clip time from the
            // global terrain clock plus a scid-hashed phase (a forest must not
            // sway in lockstep), so there's no per-instance sim state at all.
            // The 80u camera gate is what keeps fh_r1's 745 sway trees cheap:
            // only the visible ring pays skinning + a draw call.
            if (_animatedProps.Count > 0)
            {
                var apCam = _camera.Position;
                foreach (var s in _animatedProps)
                {
                    if (s.Hidden) continue;
                    var appos = s.CurrentTransform.Translation;
                    float apdx = appos.X - apCam.X, apdz = appos.Z - apCam.Z;
                    if (apdx * apdx + apdz * apdz > 80f * 80f) continue;
                    if (IsPosInFadedSnode(appos)) continue;
                    var apClips = s.Actor.Clips;
                    if (apClips.Length == 0) continue;
                    var apClip = apClips[Math.Min(s.Actor.CurrentClipIndex, apClips.Length - 1)];
                    float apT = apClip.AnimLength > 0f
                        ? (float)((_terrainTime + (s.Actor.Instance.Scid % 97) * 0.211) % apClip.AnimLength)
                        : 0f;
                    int apBones = s.Actor.Mesh.BoneCount;
                    if (_skinScratch.Length < apBones)
                        _skinScratch = new Matrix4x4[Math.Max(apBones, 64)];
                    // SC-PROP-ANIM-ANCHOR — scenery roots stay planted; the
                    // clip's constant root offset lifted trees onto visible
                    // base discs ("coaster"), see AnimationRuntime overload.
                    AnimationRuntime.ComputeSkinMatrices(s.Actor.Mesh, apClip, apT, _skinScratch, anchorRoot: true);
                    float apScale = s.Actor.Stats.RenderScale;
                    _skinShader.SetMatrix4("uModel",
                        apScale == 1f ? s.CurrentTransform
                                      : Matrix4x4.CreateScale(apScale) * s.CurrentTransform);
                    _skinShader.SetMatrix4Array("uBones[0]", _skinScratch.AsSpan(0, apBones));
                    var apSubs = s.Actor.Mesh.Subsets;
                    if (apSubs.Length == 0)
                    {
                        BindActorSlot(s.Actor, 0);
                        s.GlMesh.Draw();
                    }
                    else
                    {
                        int apLastSlot = -1;
                        foreach (var sub in apSubs)
                        {
                            if (sub.TextureIndex != apLastSlot)
                            {
                                BindActorSlot(s.Actor, sub.TextureIndex);
                                apLastSlot = sub.TextureIndex;
                            }
                            s.GlMesh.DrawSubset(sub.FirstTriangle, sub.TriangleCount);
                        }
                    }
                }
                if (_skinLayer2Active) { ResetAnimatedTextureBinding(_skinShader); _skinLayer2Active = false; }
            }

            // Phase 21d-2a-vii — layered equipment pass. Each entry is a skinned
            // ASP that shares the player body's biped skeleton; we re-skin it
            // against the body's current animation clip + time, then bind the
            // equipment texture and draw. AnimationRuntime caches its bone-name
            // → PRS-bone-index map per (asp, anim) pair, so the only per-frame
            // cost per layer is the skin matrix walk + one DrawSubset per
            // subset. We reuse _skinScratch — bones get overwritten with the
            // layer's skin matrices, then the layer issues its draws, then the
            // weapon-attach pass below recomputes bone worlds from a fresh walk.
            if (_player is not null && !_player.IsDead && _equippedLayers.Count > 0)
            {
                var pcMesh = _player.Actor.Mesh;
                var pcClips = _player.Actor.Clips;
                int pcCidx;
                if (pcClips.Length > 0)
                {
                    if (_player.IsMoving && _player.Actor.WalkClipIndex >= 0
                        && _player.Actor.WalkClipIndex < pcClips.Length)
                        pcCidx = _player.Actor.WalkClipIndex;
                    else
                        pcCidx = Math.Min(_player.Actor.CurrentClipIndex, pcClips.Length - 1);
                }
                else pcCidx = -1;

                var pcClip = pcCidx >= 0 ? pcClips[pcCidx] : null;
                var pcTime = pcClip is not null && pcClip.AnimLength > 0f
                    ? (float)(_player.AnimTime % pcClip.AnimLength)
                    : 0f;

                // _skinShader is bound + uViewProj/lighting/uAlbedo are set by
                // the body actor loop above today; re-set them here defensively
                // so this block stays correct if a future pass slips between
                // the two and clobbers the skin-shader state.
                _skinShader.Use();
                _skinShader.SetMatrix4("uViewProj", vp);
                _skinShader.SetInt("uAlbedo", 0);
                ApplyLightingUniforms(_skinShader);
                _skinShader.SetMatrix4("uModel", _player.CurrentTransform);
                int layerFlipV = 0;
                _skinShader.SetInt("uFlipV", layerFlipV);

                foreach (var layer in _equippedLayers)
                {
                    int boneCount = layer.Asp.BoneCount;
                    if (boneCount == 0) continue;
                    // Defensive cap: SkinnedMesh ctor already rejects ASPs over
                    // MaxBones, so reaching here with boneCount>64 means the
                    // SkinnedMesh and its source ASP have drifted out of sync.
                    // Skip rather than upload past the uBones[64] array end.
                    if (boneCount > SkinnedMesh.MaxBones)
                    {
                        Console.WriteLine(
                            $"  equip: '{layer.MeshBaseName}.asp' bones={boneCount} > " +
                            $"SkinnedMesh.MaxBones={SkinnedMesh.MaxBones}; skipping layer draw");
                        continue;
                    }
                    if (_skinScratch.Length < boneCount)
                        _skinScratch = new Matrix4x4[Math.Max(boneCount, 64)];
                    if (pcClip is not null)
                    {
                        AnimationRuntime.ComputeSkinMatrices(layer.Asp, pcClip, pcTime, _skinScratch);
                    }
                    else
                    {
                        for (int i = 0; i < boneCount; i++) _skinScratch[i] = Matrix4x4.Identity;
                    }
                    _skinShader.SetMatrix4Array("uBones[0]", _skinScratch.AsSpan(0, boneCount));

                    var subsets = layer.Asp.Subsets;
                    if (subsets.Length == 0)
                    {
                        if (layer.Texture is not null)
                        {
                            layer.Texture.Bind(TextureUnit.Texture0);
                            _skinShader.SetInt("uHasTexture", 1);
                        }
                        else _skinShader.SetInt("uHasTexture", 0);
                        layer.Mesh.Draw();
                    }
                    else
                    {
                        // Track first-iteration with a bool, not a sentinel int:
                        // sub.TextureIndex == -1 is a valid "use BMSH default"
                        // signal and would silently skip the bind branch if we
                        // initialised lastSlot to -1.
                        bool firstSubset = true;
                        int lastSlot = 0;
                        foreach (var sub in subsets)
                        {
                            if (firstSubset || sub.TextureIndex != lastSlot)
                            {
                                // Equipment ASPs ship a single template-driven
                                // texture (b_a_boot_<style>); when a multi-subset
                                // boot ASP shows up we bind the same texture for
                                // every subset since defend.armor_style is one
                                // value. Fall back to the ASP's BMSH default for
                                // out-of-range slots.
                                GlTexture? tex = layer.Texture;
                                if (tex is null && sub.TextureIndex >= 0
                                    && sub.TextureIndex < layer.Asp.TextureNames.Count)
                                {
                                    tex = LoadTexsetTexture(layer.Asp.TextureNames[sub.TextureIndex]);
                                }
                                if (tex is not null) { tex.Bind(TextureUnit.Texture0); _skinShader.SetInt("uHasTexture", 1); }
                                else _skinShader.SetInt("uHasTexture", 0);
                                lastSlot = sub.TextureIndex;
                                firstSubset = false;
                            }
                            layer.Mesh.DrawSubset(sub.FirstTriangle, sub.TriangleCount);
                        }
                    }
                }
            }
        }

        // Phase 21c — static prop layer (trees, barrels, fences, crops, candles,
        // chairs, etc. from non_interactive/container/inventory/interactive/emitter
        // .gas). Drawn through the same static-mesh pipeline weapons use; one draw
        // call per placement, with the texture cached per AspMesh so adjacent props
        // sharing a model only rebind once. Cull-face is disabled for the pass so
        // foliage leaf cards render from both sides (DS1 trees/shrubs/crops are
        // alpha-cutout single-sided quads; backface culling makes half the leaves
        // disappear). The fragment shader handles the alpha discard; no blend
        // state needed for hard cutout.
        if (_meshShader is not null && _staticProps.Count > 0)
        {
            _gl!.Disable(GLEnum.CullFace);
            _meshShader.Use();
            _meshShader.SetMatrix4("uViewProj", vp);
            _meshShader.SetInt("uAlbedo", 0);
            // Static-prop UVs are authored against the same orientation the .raw is
            // stored in, so the D3D→GL V flip we do for skinned meshes overshoots
            // here and lands the lid art on the body. Opt out for this pass only.
            _meshShader.SetInt("uFlipV", 0);
            ApplyLightingUniforms(_meshShader);
            GlTexture? lastTex = null;
            int lastHas = -1;
            foreach (var prop in _staticProps)
            {
                // Phase 17-SC-K — once a breakable prop's life hits zero we
                // simulate the shatter by dropping it from the render set;
                // the on-hit code already kicked debris particles at the
                // origin so the disappearance reads as a break, not a pop.
                if (prop.IsDestroyed) continue;
                // SC-PROP-INVISIBLE — authored-invisible logic objects
                // (shrines, blockers) never draw their dev-marker mesh.
                if (prop.ForceNoRender) continue;
                // SC-REGION-LAYER-HIDE — drop props belonging to regions
                // above the player's current region. Same flip cadence as
                // the terrain gate; stable across in-region movement,
                // only restored on region change back.
                if (IsAbovePlayer(prop.RegionPath)) continue;
                // SC-FADE-GROUPS — hide with the anchor snode's fade state.
                if (prop.NodeGuid != 0 && _fadedSnodeCounts.Count > 0
                    && _fadedSnodeCounts.ContainsKey(prop.NodeGuid)) continue;
                if (!ReferenceEquals(prop.Texture, lastTex))
                {
                    if (prop.Texture is not null)
                    {
                        prop.Texture.Bind(TextureUnit.Texture0);
                        if (lastHas != 1) { _meshShader.SetInt("uHasTexture", 1); lastHas = 1; }
                    }
                    else
                    {
                        if (lastHas != 0) { _meshShader.SetInt("uHasTexture", 0); lastHas = 0; }
                    }
                    lastTex = prop.Texture;
                }
                Matrix4x4 model = prop.World;
                if (prop.SpinRadPerSec != 0f)
                {
                    var theta = (float)(_terrainTime * prop.SpinRadPerSec);
                    var spin = prop.SpinAxis switch
                    {
                        1 => Matrix4x4.CreateRotationY(theta),
                        2 => Matrix4x4.CreateRotationZ(theta),
                        _ => Matrix4x4.CreateRotationX(theta),
                    };
                    model = spin * prop.World;
                }
                // SC-DOORS-OPEN — apply door rotation per state, in LOCAL
                // space (multiplied BEFORE the placement transform) so it
                // pivots around the authored asp origin (the hinge, at
                // local X=0). DS1 door .asp geometry is authored Z-up — the
                // height runs along Z and the placement world bakes the
                // Z-up→Y-up standup — so the vertical hinge axis in this
                // pre-world frame is Z, not Y. Rotating around Y instead
                // swept the leaf through the width×height plane: the door
                // spun like a clock dial rather than swinging out.
                if (prop.IsDoor && prop.DoorOpenFrac > 0.001f)
                {
                    // Wall doors hinge on the vertical Z axis; bulkhead/storm
                    // doors (DoorIsFlip) lie in the X-Y plane and hinge on the
                    // horizontal Y edge at the seam, flipping the outer edge up.
                    float ang = prop.DoorSwingSign * prop.DoorOpenFrac * (MathF.PI / 2f);
                    var swing = prop.DoorIsFlip
                        ? Matrix4x4.CreateRotationY(ang)
                        : Matrix4x4.CreateRotationZ(ang);
                    model = swing * prop.World;
                }
                _meshShader.SetMatrix4("uModel", model);
                prop.Mesh.Draw();
            }
            _gl.Enable(GLEnum.CullFace);
        }

        // Phase 21-SC-BARREL-C — frag-debris pass. Same shader and uFlipV
        // convention as the static-prop layer (frag .asps are authored
        // bottom-up like every other DS1 prop), but the model matrix is
        // rebuilt per frame from the integrated position + spin axis.
        // Cull-face stays enabled — frag fragments are solid little
        // bone/wood/metal chunks, not foliage cards.
        if (_meshShader is not null && _fragDebris.Count > 0)
        {
            _meshShader.Use();
            _meshShader.SetMatrix4("uViewProj", vp);
            _meshShader.SetInt("uAlbedo", 0);
            _meshShader.SetInt("uFlipV", 0);
            ApplyLightingUniforms(_meshShader);
            GlTexture? lastTex = null;
            int lastHas = -1;
            foreach (var f in _fragDebris)
            {
                if (!ReferenceEquals(f.Asset.Texture, lastTex))
                {
                    if (f.Asset.Texture is not null)
                    {
                        f.Asset.Texture.Bind(TextureUnit.Texture0);
                        if (lastHas != 1) { _meshShader.SetInt("uHasTexture", 1); lastHas = 1; }
                    }
                    else if (lastHas != 0) { _meshShader.SetInt("uHasTexture", 0); lastHas = 0; }
                    lastTex = f.Asset.Texture;
                }
                var spin = Matrix4x4.CreateFromAxisAngle(f.SpinAxis, f.SpinAngle);
                var model = spin * Matrix4x4.CreateTranslation(f.Pos);
                _meshShader.SetMatrix4("uModel", model);
                f.Asset.Mesh.Draw();
            }
        }

        // SC-NPC-WEAPONS — draw each armed NPC's held gear with the static-mesh
        // pipeline, attached RAW to the animated weapon/shield bone (same
        // authored-attach rule as the player's weapon below: DS1 weapon meshes
        // are modeled with mesh space as the hand frame). Clip/time selection
        // mirrors the skinned-body loop exactly so the club tracks the same
        // pose the body was skinned with; dead actors hold the die-clamp pose
        // with the weapon still in hand unless it dropped into the loot pile.
        if (_meshShader is not null)
        {
            bool npcGearShaderBound = false;
            foreach (var s in _actors)
            {
                if (s.IsPlayer || s.Hidden) continue;
                if (s.WeaponMesh is null && s.ShieldMesh is null) continue;
                // SC-REGION-LAYER-HIDE-ACTORS (follow-up) — no region stamp on
                // actors yet; gear hides with the faded-snode gate below.
                if (IsPosInFadedSnode(s.CurrentTransform.Translation)) continue;
                var npcClips = s.Actor.Clips;
                int npcBones = s.Actor.Mesh.BoneCount;
                if (_boneWorldsScratch.Length < npcBones)
                    _boneWorldsScratch = new Matrix4x4[Math.Max(npcBones, 64)];
                if (npcClips.Length == 0)
                {
                    AnimationRuntime.ComputeAnimatedBoneWorlds(s.Actor.Mesh, null, 0f, _boneWorldsScratch);
                }
                else
                {
                    int cidx;
                    if (!s.IsDead && s.IsMoving && !s.Actor.Host.IsOverrideActive
                        && s.Actor.WalkClipIndex >= 0 && s.Actor.WalkClipIndex < npcClips.Length)
                        cidx = s.Actor.WalkClipIndex;
                    else
                        cidx = Math.Min(s.Actor.CurrentClipIndex, npcClips.Length - 1);
                    var npcClip = npcClips[cidx];
                    float nt;
                    if (npcClip.AnimLength <= 0f) nt = 0f;
                    else if (s.IsDead) nt = MathF.Min((float)s.AnimTime, npcClip.AnimLength - 0.01f);
                    else nt = (float)(s.AnimTime % npcClip.AnimLength);
                    AnimationRuntime.ComputeAnimatedBoneWorlds(s.Actor.Mesh, npcClip, nt, _boneWorldsScratch);
                }
                float npcScale = s.Actor.Stats.RenderScale;
                var actorModel = npcScale == 1f
                    ? s.CurrentTransform
                    : Matrix4x4.CreateScale(npcScale) * s.CurrentTransform;
                void DrawGear(StaticMesh? gearMesh, GlTexture? gearTex, int boneIdx)
                {
                    if (gearMesh is null || boneIdx < 0 || boneIdx >= npcBones) return;
                    if (!npcGearShaderBound)
                    {
                        _meshShader.Use();
                        _meshShader.SetMatrix4("uViewProj", vp);
                        _meshShader.SetInt("uAlbedo", 0);
                        _meshShader.SetInt("uFlipV", 0);
                        ApplyLightingUniforms(_meshShader);
                        npcGearShaderBound = true;
                    }
                    _meshShader.SetMatrix4("uModel", _boneWorldsScratch[boneIdx] * actorModel);
                    if (gearTex is not null)
                    {
                        gearTex.Bind(TextureUnit.Texture0);
                        _meshShader.SetInt("uHasTexture", 1);
                    }
                    else _meshShader.SetInt("uHasTexture", 0);
                    gearMesh.Draw();
                }
                if (!s.WeaponDroppedOnDeath) DrawGear(s.WeaponMesh, s.WeaponTexture, s.WeaponBoneIdx);
                DrawGear(s.ShieldMesh, s.ShieldTexture, s.ShieldBoneIdx);
                // SC-TORCH-FLAME — a held torch burns: sparse small flame
                // motes at the torch head (weapon bone + up). Built — needs
                // eyes for size/height tuning.
                if (!s.IsDead && !s.WeaponDroppedOnDeath && s.WeaponMesh is not null
                    && s.WieldedWeaponRef is { } wref
                    && wref.Contains("torch", StringComparison.OrdinalIgnoreCase)
                    && s.WeaponBoneIdx >= 0 && s.WeaponBoneIdx < npcBones
                    && _particles is not null && _pileSparkleRng.NextDouble() < 0.30)
                {
                    var torchTip = Vector3.Transform(new Vector3(0f, 0.45f, 0f),
                        _boneWorldsScratch[s.WeaponBoneIdx] * actorModel);
                    _particles.SpawnSmoke(torchTip,
                        _pileSparkleRng.NextDouble() < 0.5
                            ? new Vector4(1.0f, 0.55f, 0.12f, 0.75f)
                            : new Vector4(1.0f, 0.85f, 0.30f, 0.70f),
                        0.14f, 0.4f, 1);
                }
            }

            // SC-RANGED-PROJECTILE — ammo GOs in flight (or stuck in the
            // ground), oriented to trajectory (arrows author
            // orient_to_trajectory = true; the velocity vector is retained
            // after sticking so the shaft keeps its landing angle).
            bool shotShaderBound = false;
            foreach (var shot in _rangedShots)
            {
                if (shot.Mesh is null) continue;
                if (!shotShaderBound)
                {
                    _meshShader.Use();
                    _meshShader.SetMatrix4("uViewProj", vp);
                    _meshShader.SetInt("uAlbedo", 0);
                    _meshShader.SetInt("uFlipV", 0);
                    ApplyLightingUniforms(_meshShader);
                    shotShaderBound = true;
                }
                var v = shot.Vel;
                float horiz = MathF.Sqrt(v.X * v.X + v.Z * v.Z);
                float pitch = MathF.Atan2(-v.Y, MathF.Max(0.001f, horiz));
                float yaw = MathF.Atan2(v.X, v.Z);
                // SC-RANGED-PROJECTILE fix — arrow ASPs are modeled along +Y
                // (user report: shaft read VERTICAL); pre-rotate +Y→+Z so
                // the trajectory pitch/yaw sees the shaft as forward.
                var model = s_ammoAxisFix
                          * Matrix4x4.CreateRotationX(pitch)
                          * Matrix4x4.CreateRotationY(yaw)
                          * Matrix4x4.CreateTranslation(shot.Pos);
                _meshShader.SetMatrix4("uModel", model);
                if (shot.Tex is not null)
                {
                    shot.Tex.Bind(TextureUnit.Texture0);
                    _meshShader.SetInt("uHasTexture", 1);
                }
                else _meshShader.SetInt("uHasTexture", 0);
                shot.Mesh.Draw();
            }
        }

        // Phase 14d — render the PC's equipped weapon attached to the weapon_grip
        // bone. We recompute the animated bone worlds for the PC (cheap — the bone
        // walk already happened inside ComputeSkinMatrices, we just didn't surface
        // the intermediate array). weaponWorld = weaponBindInv * worldAnim[weapon_grip]
        // * player.CurrentTransform. Drawn with the static-mesh pipeline because
        // DS1 weapon ASPs weight every corner to bone 0 (effectively rigid); the
        // bind-inverse pre-multiply is what snaps the ASP's grip bone onto the hand.
        // The intro hoe beat can pin the weapon mesh to the off-hand (shield_grip) via
        // _weaponGripBoneOverride; otherwise it rides weapon_grip as normal.
        int gripIdx = _weaponGripBoneOverride >= 0 ? _weaponGripBoneOverride : _weaponGripBoneIdx;
        if (_meshShader is not null && _weaponMesh is not null && _player is not null
            && !_player.IsDead && gripIdx >= 0)
        {
            var pcMesh = _player.Actor.Mesh;
            var clips = _player.Actor.Clips;
            int pcBones = pcMesh.BoneCount;
            // Phase 21b-2 — reuse a shared bone-world scratch instead of allocating
            // a fresh Matrix4x4[] every frame just to read one element.
            if (_boneWorldsScratch.Length < pcBones)
                _boneWorldsScratch = new Matrix4x4[Math.Max(pcBones, 64)];
            if (clips.Length == 0)
            {
                AnimationRuntime.ComputeAnimatedBoneWorlds(pcMesh, null, 0f, _boneWorldsScratch);
            }
            else
            {
                // Mirror the body's walk-swap so the weapon's grip bone tracks the same
                // clip the body is being skinned with. Without this the body uses the
                // walk clip while the weapon's bone-worlds are still sampled from the
                // idle clip — symptom: dagger "floats" at the idle wrist position
                // while the walking arm swings through it.
                int cidx = _player.Actor.CurrentClipIndex;
                if (_player.IsMoving && _player.Actor.WalkClipIndex >= 0 && _player.Actor.WalkClipIndex < clips.Length)
                    cidx = _player.Actor.WalkClipIndex;
                cidx = Math.Min(cidx, clips.Length - 1);
                var clip = clips[cidx];
                var t = (float)(clip.AnimLength > 0f ? _player.AnimTime % clip.AnimLength : 0.0);
                AnimationRuntime.ComputeAnimatedBoneWorlds(pcMesh, clip, t, _boneWorldsScratch);
            }

            // SC-CAST-STOW — a selected spell slot puts the weapon away
            // (DS1 casts unarmed or with a staff; the staff stays in hand).
            // SC-POTION-DRINK-BOTTLE — the drink gesture swaps the weapon for the
            // potion bottle, so the weapon hides for the whole raise.
            if (gripIdx < pcBones && !PlayerDrinking && !(SpellSlotActive && !_weaponIsStaff))
            {
                var gripLocal = _boneWorldsScratch[gripIdx];
                // weaponBindInv cancels the weapon ASP's own grip-bone bind offset
                // so the grip sits at the hand bone's world origin, then gripLocal
                // places it in the player mesh frame, then CurrentTransform moves
                // the whole rig to world space.
                //
                // SiegeMax ASPImport.ms ("grips must be prerotated (hack fix)",
                // line 765) applies an `angleAxis 90 [1,0,0]` to weapon_grip /
                // shield_grip on import; PRS keys are authored against that frame.
                // Without the same rotation at draw time the dagger sits 90° off —
                // blade points down (icepick / "stabbing" grip) instead of forward
                // (thrust / "piercing" grip). s_gripPreRot / s_gripPreTrans baked
                // from 21d-2a-vi A/B work — see field-level comment for context.
                var gripPreRot = Matrix4x4.CreateFromQuaternion(s_gripPreRot);
                var gripPreTrans = Matrix4x4.CreateTranslation(s_gripPreTrans);
                Matrix4x4 weaponModel;
                if (_introHoeSwapped)
                {
                    // Hoe in hand: layer the dev-tunable grip offset (numpad) on top of the
                    // base grip so its angle/twist can be dialed in live, then baked.
                    const float d2r = MathF.PI / 180f;
                    // Baked grip pose + live dev delta (numpad tuner). Rotation in the hoe's local
                    // frame; position in PLAYER space (after the hand-bone world, before the
                    // player->world transform) so the axes map to the hero (Y up/down, X across,
                    // Z fore/aft) rather than the hoe's tilted local axes.
                    var euler = s_hoeGripEulerDeg + _hoeGripEulerDeg;
                    var hoeRot = Matrix4x4.CreateFromYawPitchRoll(euler.Y * d2r, euler.X * d2r, euler.Z * d2r);
                    var hoeTrans = Matrix4x4.CreateTranslation(s_hoeGripTrans + _hoeGripTrans);
                    weaponModel = _weaponBindInv * hoeRot * gripPreRot * gripPreTrans * gripLocal * hoeTrans * _player.CurrentTransform;
                }
                else
                {
                    // SC-ATTACK-GRIP (Phase 18a) — authored attach: DS1 weapon
                    // meshes are modeled with MESH SPACE AS THE HAND FRAME
                    // (dagger/sword origin at the pommel, staff and bow at
                    // their center grip — verified across every family), so
                    // the weapon attaches RAW to the animated weapon_grip
                    // bone. The old bindInv·gripPreRot·gripPreTrans chain
                    // algebraically cancels to ~identity for the dagger the
                    // constants were tuned on (inverse(R180x·T(0,.038,0)) ·
                    // R180x · T(.02,.04,0) = T(.02,.002,0)) — but left every
                    // OTHER weapon offset by its own grip-bone bind delta
                    // (mace 9cm low). Raw attach is what the tuning was
                    // converging on; the GRIP bone bind matters only for
                    // skinning, not attachment.
                    weaponModel = gripLocal * _player.CurrentTransform;
                }

                if (_introHoeSwapped && !_hoeRenderDiagLogged)
                {
                    var hw = weaponModel.Translation;
                    var pw = _player.CurrentTransform.Translation;
                    Console.WriteLine($"[hoe] render firing: hoe world=({hw.X:F2},{hw.Y:F2},{hw.Z:F2}) " +
                                      $"player=({pw.X:F2},{pw.Y:F2},{pw.Z:F2}) gripIdx={gripIdx} clip={_player.Actor.CurrentClipIndex}/{clips.Length}");
                    _hoeRenderDiagLogged = true;
                }

                _meshShader.Use();
                _meshShader.SetMatrix4("uViewProj", vp);
                _meshShader.SetMatrix4("uModel", weaponModel);
                _meshShader.SetInt("uAlbedo", 0);
                // 21d-2a-vi: weapon textures (e.g. b_w_weapons.raw, a 9-mip atlas
                // covering daggers/swords/axes/staves) are stored bottom-up like
                // every other DS1 .raw. uFlipV=1 was inverting the dagger's UV
                // V[0.667,0.998] into V[0.002,0.333], which sampled a magic-staff
                // glow strip at the bottom of the atlas — visible as a "rainbow"
                // dagger. Match the skinned-actor / static-prop convention.
                _meshShader.SetInt("uFlipV", 0);
                ApplyLightingUniforms(_meshShader);
                if (_weaponTexture is not null)
                {
                    _weaponTexture.Bind(TextureUnit.Texture0);
                    _meshShader.SetInt("uHasTexture", 1);
                }
                else
                {
                    _meshShader.SetInt("uHasTexture", 0);
                }
                _weaponMesh.Draw();

                // SC-RANGED-PROJECTILE / SC-BOW-NOCK — the nocked arrow.
                // DS1 keeps an arrow nocked the WHOLE time the bow is the
                // active weapon (idle, walking, winding up); it vanishes
                // only between the FIRE note (the shot takes flight) and
                // the next iteration's re-nock. Orientation comes from the
                // PLAYER'S FACING — the same horizontal frame the shot
                // launches in — NOT the raw hand-bone frame, whose local
                // axes stood the shaft straight up (the "double arrows,
                // one vertical" report: vertical nock + correct flight).
                if (_weaponIsRanged && _playerAmmoMesh is not null
                    && (_playerSwing is null || !_playerSwing.ArrowAway))
                {
                    var handPos = weaponModel.Translation;
                    var nockFwd = Vector3.TransformNormal(Vector3.UnitZ, _player.CurrentTransform);
                    float nockYaw = MathF.Atan2(nockFwd.X, nockFwd.Z);
                    // The arrow ASP's origin is the SHAFT CENTER (end bones
                    // at Z −0.45/+0.44); anchored raw on the hand, the back
                    // half buried itself in the chest. Half a shaft forward
                    // puts the NOCK end at the bow hand.
                    var nockModel = Matrix4x4.CreateRotationY(nockYaw)
                                  * Matrix4x4.CreateTranslation(handPos + nockFwd * 0.45f);
                    _meshShader.SetMatrix4("uModel", nockModel);
                    if (_playerAmmoTex is not null)
                    {
                        _playerAmmoTex.Bind(TextureUnit.Texture0);
                        _meshShader.SetInt("uHasTexture", 1);
                    }
                    else _meshShader.SetInt("uHasTexture", 0);
                    _playerAmmoMesh.Draw();
                }
            }

            // Phase 9-SC-10 — render every other bone-attached prop on the PC
            // (shields today, quiver/torch tomorrow). Reuses the per-frame
            // _boneWorldsScratch the weapon draw just populated, so we don't
            // recompute the bone walk; each attach takes one bone from the
            // already-animated array, applies its bind-inverse + the shared
            // grip prerotation/translation, and draws as a static mesh. The
            // same prerotation rule applies to shield_grip per SiegeMax
            // ASPImport.ms (see s_gripPreRot field comment).
            if (_attachedItems.Count > 0 && _meshShader is not null)
            {
                int pcBoneCount = _player.Actor.Mesh.BoneCount;
                var gripPreRot = Matrix4x4.CreateFromQuaternion(s_gripPreRot);
                var gripPreTrans = Matrix4x4.CreateTranslation(s_gripPreTrans);
                _meshShader.Use();
                _meshShader.SetMatrix4("uViewProj", vp);
                _meshShader.SetInt("uAlbedo", 0);
                _meshShader.SetInt("uFlipV", 0);
                ApplyLightingUniforms(_meshShader);
                // Phase 9-SC-10 — extra rotation for the shield slot, applied on
                // top of the shared grip prerotation. See s_shieldExtraRot for the
                // rationale behind the +85° X bake.
                var shieldExtraRot = Matrix4x4.CreateFromQuaternion(s_shieldExtraRot);
                var shieldExtraTrans = Matrix4x4.CreateTranslation(s_shieldExtraTrans);
                foreach (var att in _attachedItems)
                {
                    if (att.BoneIdx < 0 || att.BoneIdx >= pcBoneCount) continue;
                    var boneLocal = _boneWorldsScratch[att.BoneIdx];
                    bool isShield = string.Equals(att.SlotName, "es_shield_hand",
                        StringComparison.OrdinalIgnoreCase);
                    // SC-PAPERDOLL-EQUIP follow-up — a wielded bow occupies
                    // the shield hand (DS1's es_shield_hand rule), so the
                    // worn shield hides while the ranged weapon is out.
                    if (isShield && _weaponIsRanged) continue;
                    var model = isShield
                        ? att.BindInv * shieldExtraRot * shieldExtraTrans * gripPreRot * gripPreTrans * boneLocal * _player.CurrentTransform
                        : att.BindInv * gripPreRot * gripPreTrans * boneLocal * _player.CurrentTransform;
                    _meshShader.SetMatrix4("uModel", model);
                    if (att.Texture is not null)
                    {
                        att.Texture.Bind(TextureUnit.Texture0);
                        _meshShader.SetInt("uHasTexture", 1);
                    }
                    else
                    {
                        _meshShader.SetInt("uHasTexture", 0);
                    }
                    att.Mesh.Draw();
                }
            }
        }

        // SC-POTION-DRINK-BOTTLE — while the raise gesture plays, the potion
        // bottle rides the hero's weapon hand, tinted by the potion type. Its
        // OWN block (not the weapon draw's) so it also shows when drinking
        // unarmed; it recomputes the animated grip-bone world the same way the
        // weapon draw does. Kept world-upright (translation only) — the ASP is
        // an inventory bottle modeled standing on +Y, so following the raw hand
        // frame would tip it over; DS1 shows it held upright at the lips.
        if (_meshShader is not null && PlayerDrinking && _bottleMesh is not null
            && _player is not null && !_player.IsDead && gripIdx >= 0)
        {
            var pcMesh = _player.Actor.Mesh;
            var clips = _player.Actor.Clips;
            int pcBones = pcMesh.BoneCount;
            if (_boneWorldsScratch.Length < pcBones)
                _boneWorldsScratch = new Matrix4x4[Math.Max(pcBones, 64)];
            if (clips.Length == 0)
            {
                AnimationRuntime.ComputeAnimatedBoneWorlds(pcMesh, null, 0f, _boneWorldsScratch);
            }
            else
            {
                int cidx = _player.Actor.CurrentClipIndex;
                if (_player.IsMoving && _player.Actor.WalkClipIndex >= 0 && _player.Actor.WalkClipIndex < clips.Length)
                    cidx = _player.Actor.WalkClipIndex;
                cidx = Math.Min(cidx, clips.Length - 1);
                var clip = clips[cidx];
                var t = (float)(clip.AnimLength > 0f ? _player.AnimTime % clip.AnimLength : 0.0);
                AnimationRuntime.ComputeAnimatedBoneWorlds(pcMesh, clip, t, _boneWorldsScratch);
            }
            if (gripIdx < pcBones)
            {
                var handPos = (_boneWorldsScratch[gripIdx] * _player.CurrentTransform).Translation;
                // SC-POTION-DRINK-BOTTLE round 2 — face the bottle's SPOUT at
                // the hero's mouth. The translation-only draw left the bottle
                // at a fixed world orientation, so the raise gesture read as
                // "drinking from the back of the bottle" at most facings.
                // Yaw from the player's facing + 180° turns the pouring lip
                // toward the face.
                var drinkFwd = Vector3.TransformNormal(Vector3.UnitZ, _player.CurrentTransform);
                float drinkYaw = MathF.Atan2(drinkFwd.X, drinkFwd.Z);
                var bottleModel = Matrix4x4.CreateRotationY(drinkYaw + MathF.PI)
                                * Matrix4x4.CreateTranslation(handPos + new Vector3(0f, 0.06f, 0f));
                var bottleTex = _drinkIsHealth ? _bottleTexHealth : _bottleTexMana;
                _meshShader.Use();
                _meshShader.SetMatrix4("uViewProj", vp);
                _meshShader.SetMatrix4("uModel", bottleModel);
                _meshShader.SetInt("uAlbedo", 0);
                _meshShader.SetInt("uFlipV", 0);
                ApplyLightingUniforms(_meshShader);
                if (bottleTex is not null)
                {
                    bottleTex.Bind(TextureUnit.Texture0);
                    _meshShader.SetInt("uHasTexture", 1);
                }
                else _meshShader.SetInt("uHasTexture", 0);
                _bottleMesh.Draw();
            }
        }

        // SC-LEVELUP-BOOK — the skill tome spinning above the hero: 350°/s
        // yaw (authored rot_inc), grows in over the first quarter, holds
        // full-size, shrinks away over the last quarter (the user-described
        // "spins up growing, hits max, spins back down shrinking").
        if (_meshShader is not null && _levelUpFxRemaining > 0f
            && _levelUpFxMesh is not null && _player is not null && !_player.IsDead)
        {
            float t = 1f - _levelUpFxRemaining / LevelUpFxSeconds; // 0 → 1
            float fxScale = t < 0.25f ? t / 0.25f
                          : t > 0.75f ? (1f - t) / 0.25f : 1f;
            if (fxScale > 0.01f)
            {
                float spin = t * LevelUpFxSeconds * (350f * MathF.PI / 180f);
                // The tome meshes are authored LYING FLAT (Y-thin, book-on-a-
                // table) with a -90°X bind that StaticMesh doesn't bake —
                // drawn raw the book floated horizontally. Apply the bind's
                // own rotation to STAND it upright (mesh Z-length becomes
                // world up), then spin about the vertical axis. The upright
                // book extends ~0.2-0.9u ABOVE its origin, so the anchor sits
                // lower than the flat version did.
                var anchor = _player.CurrentTransform.Translation + new Vector3(0f, 1.55f, 0f);
                var fxModel = Matrix4x4.CreateScale(fxScale)
                            * Matrix4x4.CreateRotationX(-MathF.PI / 2f)
                            * Matrix4x4.CreateRotationY(spin)
                            * Matrix4x4.CreateTranslation(anchor);
                _meshShader.Use();
                _meshShader.SetMatrix4("uViewProj", vp);
                _meshShader.SetMatrix4("uModel", fxModel);
                _meshShader.SetInt("uAlbedo", 0);
                _meshShader.SetInt("uFlipV", 0);
                ApplyLightingUniforms(_meshShader);
                if (_levelUpFxMesh.Texture is not null)
                {
                    _levelUpFxMesh.Texture.Bind(TextureUnit.Texture0);
                    _meshShader.SetInt("uHasTexture", 1);
                }
                else _meshShader.SetInt("uHasTexture", 0);
                _levelUpFxMesh.Mesh.Draw();
            }
        }

        // Phase 9-SC-7 — render the first item in each loot pile as its real
        // ASP mesh (and texture, if any). Items without [aspect][model] (gold,
        // potions whose template is scroll-only, etc.) fall back to the legacy
        // untextured cube so every pile is still visually distinct from terrain.
        // Phase 9-SC-LL — same loop captures the per-pile world-space label so
        // the text-overlay pass can draw "Spiked Club" / "Healing Potion" /
        // etc. above each pile (gold, red on hover) without re-resolving.
        _frameLootLabels.Clear();
        if (_meshShader is not null && _lootPiles.Count > 0)
        {
            _meshShader.Use();
            _meshShader.SetMatrix4("uViewProj", vp);
            _meshShader.SetInt("uAlbedo", 0);
            ApplyLightingUniforms(_meshShader);
            const float pileSize = 0.5f;
            const float itemScale = 0.6f; // matches the cube footprint visually
            // SC-WORLD-INVENTORY-VIEW-DISTANCE — radius shared with the
            // glitter tick (see WorldInventoryVisRadius). Enemy-death drops
            // and player-throw piles aren't world-inventory so they continue
            // to render at unlimited range.
            float worldInvR2 = WorldInventoryVisRadius * WorldInventoryVisRadius;
            Vector3 playerPos = _player?.CurrentTransform.Translation ?? Vector3.Zero;
            bool havePlayer = _player is not null;
            foreach (var pile in _lootPiles)
            {
                if (pile.IsWorldInventory && havePlayer)
                {
                    float pdx = pile.Position.X - playerPos.X;
                    float pdz = pile.Position.Z - playerPos.Z;
                    if (pdx * pdx + pdz * pdz > worldInvR2) continue;
                }
                // SC-FADE-GROUPS — piles sitting in a faded-out layer hide
                // with their terrain (a glittering surface pile would
                // otherwise float over the void during the cellar cutaway).
                if (IsPosInFadedSnode(pile.Position)) continue;
                var pos = pile.Position;
                // Walk the pile rather than only trying Items[0] — DS1 mixes
                // resolvable templates ("wpn_axe_001") with pcontent specs
                // ("#weapon/9") in the same drop list, and Items[0] is often
                // the unrollable spec. First resolvable wins; fall through to
                // the cube only if every entry misses.
                ItemMesh? itemMesh = null;
                // ALPHA-2C — DisplayOverride wins (world gold renders the
                // authored coin mesh; its entry is a synthetic amount).
                if (pile.DisplayOverride.Length > 0)
                    itemMesh = TryGetItemMesh(pile.DisplayOverride);
                bool goldDisplay = false;
                // World-placed gold routes through DisplayOverride (the `gold`
                // template = m_i_glb_coin-01); flag it so it gets the same
                // lay-flat coin transform as enemy-drop gold, not the upright
                // item transform (which would stand the coin fan on edge).
                if (itemMesh is not null && pile.DisplayOverride.Length > 0
                    && pile.DisplayOverride.Contains("gold", StringComparison.OrdinalIgnoreCase))
                    goldDisplay = true;
                for (var i = 0; itemMesh is null && i < pile.Items.Count; i++)
                {
                    // SC-GOLD-PILES — synthetic gold entries ("7-7") have no
                    // template; display DS1's authored flat COIN SPREAD sized by
                    // amount. DS1's own `gold` GO template ships
                    // model = m_i_glb_coin-01 (mesh b_i_glb_gold) — a flat,
                    // ground-hugging fan of coins, NOT the chunky cavern treasure
                    // nuggets (m_i_cav_gold-*, used only for dungeon treasure
                    // rooms). coin-01/02/03 grow the fan with the amount (5/16/40
                    // corners). This is the user's gold.png reference look; the
                    // small footprint is DS1-authentic (the star glint + "N gold"
                    // label are what make it pop at camera distance).
                    if (pile.Items[i].IsGold)
                    {
                        // No template references these meshes by our synthetic
                        // amount, so resolve the coin model by name directly.
                        // SC-GOLD-PILES-ROBUST — a gold drop must ALWAYS read as
                        // gold, never the debug cube: amount-sized coin fan, then
                        // the other coin sizes, then the `gold` template (also
                        // m_i_glb_coin-01), then the nugget as a last resort.
                        var (glo, ghi) = pile.Items[i].GoldRange();
                        int amt = (glo + ghi) / 2;
                        string primary = amt < 25 ? "m_i_glb_coin-01"
                                       : amt < 100 ? "m_i_glb_coin-02" : "m_i_glb_coin-03";
                        itemMesh = TryGetModelMesh(primary)
                                ?? TryGetModelMesh("m_i_glb_coin-02")
                                ?? TryGetModelMesh("m_i_glb_coin-01")
                                ?? TryGetModelMesh("m_i_glb_coin-03")
                                ?? TryGetItemMesh("gold")
                                ?? TryGetModelMesh("m_i_glb_nugget-gold-01");
                        goldDisplay = itemMesh is not null;
                        if (_loggedGoldResolve.Add(primary))
                            Console.WriteLine(itemMesh is not null
                                ? $"  gold-mesh: amt={amt} → {(goldDisplay ? primary : "fallback")} OK"
                                : $"  gold-mesh: amt={amt} ALL candidates missed → cube (report this)");
                    }
                    else
                    {
                        itemMesh = TryGetItemMesh(pile.Items[i].Reference);
                    }
                }
                // SC-GOLD-GLINT — retail gold GLITTERS like scattered starlight,
                // not the puffs of smoke SpawnSmoke gave (user: "buffs of smoke
                // instead of glitter"). SpawnTwinkle emits the star sprite
                // (TexSlot 2), additive, grow-then-fade, drifting across the coin
                // fan's footprint — a proper sparkle field hugging the pile.
                if (_particles is not null && PileIsGoldOrSpell(pile)
                    && pile.Items.Any(x => x.IsGold)
                    && _pileSparkleRng.NextDouble() < 0.06)
                {
                    _particles.SpawnTwinkle(pos + new Vector3(0f, 0.07f, 0f),
                        new Vector4(1.0f, 0.94f, 0.55f, 1f), 0.28f, 0.09f, 0.5f, 2);
                }

                if (itemMesh is not null)
                {
                    // Phase 9-SC-10 — RestPitch tips items that don't sit
                    // naturally upright (shields lay flat). While a throw is
                    // in flight, fade the pitch in over the back half so it
                    // doesn't pop on landing.
                    float pitch = pile.RestPitch;
                    if (pile.Throw is { } th2 && th2.Duration > 0f)
                    {
                        float pt = MathF.Min(1f, th2.Elapsed / th2.Duration);
                        pitch *= MathF.Max(0f, (pt - 0.5f) * 2f);
                    }
                    // Phase 21-SC-SCROLL-F — sum the static rest-pitch with the
                    // throw-driven X tumble so a flying item reads as "twist
                    // + flop" while a landed item just sits at its rest angle.
                    // RotationX is already 0 once the throw lands (cleared at
                    // t>=1 in the tick), so this collapses to RestPitch only.
                    // Gold coin fans are authored on the mesh's XY plane (Z-up,
                    // like every DS1 item ASP) with a -90°X bind that StaticMesh
                    // doesn't bake, so without a rotation here the fan stands
                    // VERTICAL. Lay it flat on the world XZ floor (mesh Z, the
                    // thin axis, becomes world up), then random-spin + ground it
                    // at full scale (no half-size lift — the fan is ~2cm thick).
                    var model = goldDisplay
                        ? Matrix4x4.CreateRotationX(-MathF.PI / 2f)
                          * Matrix4x4.CreateRotationY(pile.RotationY)
                          * Matrix4x4.CreateTranslation(pos.X, pos.Y + 0.02f, pos.Z)
                        : Matrix4x4.CreateScale(itemScale)
                          * Matrix4x4.CreateRotationX(pitch + pile.RotationX)
                          * Matrix4x4.CreateRotationY(pile.RotationY)
                          * Matrix4x4.CreateTranslation(pos.X, pos.Y + pileSize * 0.5f, pos.Z);
                    _meshShader.SetMatrix4("uModel", model);
                    _meshShader.SetInt("uFlipV", 0);
                    if (itemMesh.Texture is not null)
                    {
                        itemMesh.Texture.Bind(TextureUnit.Texture0);
                        _meshShader.SetInt("uHasTexture", 1);
                    }
                    else
                    {
                        _meshShader.SetInt("uHasTexture", 0);
                    }
                    itemMesh.Mesh.Draw();
                    if (itemMesh.DisplayName is not null)
                    {
                        // SC-LOOT-LABEL-RANGE — labels only within a readable
                        // radius of the PLAYER; distant piles showed a wall of
                        // floating text across the whole screen. 14u ≈ the
                        // area you'd plausibly walk to next (user-requested
                        // "reasonable distance limit").
                        float lblDx = pos.X - playerPos.X, lblDz = pos.Z - playerPos.Z;
                        if (lblDx * lblDx + lblDz * lblDz <= 14f * 14f)
                        {
                            // Sit the label nearly on top of the mesh — a 0.05
                            // gap floats the text just above the surface.
                            var labelPos = new Vector3(pos.X, pos.Y + pileSize + 0.05f, pos.Z);
                            _frameLootLabels.Add((labelPos, itemMesh.DisplayName));
                        }
                    }
                }
                else if (_lootCube is not null && !pile.Items.Any(x => x.IsGold))
                {
                    // SC-GOLD-PILES-ROBUST — the debug cube stands in for a
                    // failed-to-resolve ITEM mesh, never for gold: a gold pile
                    // that somehow missed every mesh candidate above still shows
                    // its glint (spawned earlier) rather than a beige box, which
                    // reads far worse than "sparkle with no pile."
                    var model = Matrix4x4.CreateScale(pileSize)
                              * Matrix4x4.CreateTranslation(pos.X, pos.Y + pileSize * 0.5f, pos.Z);
                    _meshShader.SetMatrix4("uModel", model);
                    _meshShader.SetInt("uFlipV", 1);
                    _meshShader.SetInt("uHasTexture", 0);
                    _lootCube.Draw();
                }
            }
        }

        // SC-EMT-GO — tumbling debris chunks (activated avalanches /
        // rockfalls). Same mesh-shader state family as the loot piles.
        if (_meshShader is not null && _goDebris.Count > 0)
        {
            _meshShader.Use();
            _meshShader.SetMatrix4("uViewProj", vp);
            _meshShader.SetInt("uAlbedo", 0);
            _meshShader.SetInt("uFlipV", 0);
            ApplyLightingUniforms(_meshShader);
            foreach (var d in _goDebris)
            {
                var dm = Matrix4x4.CreateFromAxisAngle(d.RotAxis, d.RotAngle)
                       * Matrix4x4.CreateTranslation(d.Pos);
                _meshShader.SetMatrix4("uModel", dm);
                if (d.Mesh.Texture is not null)
                {
                    d.Mesh.Texture.Bind(TextureUnit.Texture0);
                    _meshShader.SetInt("uHasTexture", 1);
                }
                else _meshShader.SetInt("uHasTexture", 0);
                d.Mesh.Mesh.Draw();
            }
        }

        if (_meshShader is not null && _regionInstances.Count > 0)
        {
            _meshShader.Use();
            _meshShader.SetMatrix4("uViewProj", vp);
            _meshShader.SetInt("uAlbedo", 0);
            _meshShader.SetInt("uFlipV", 1);
            _meshShader.SetInt("uUvOrient", _terrainUvOrient);
            ApplyLightingUniforms(_meshShader);
            _meshShader.SetInt("uUseBakedLight", 1); // ALPHA-2 RADIOSITY — terrain = authored bake
            _meshShader.SetInt("uAlbedo2", 1);
            // SC-TSD-ANIM draw-order — split the region pass in two: pass 1
            // draws every subset that resolves to a single-layer TSD (or has
            // no TSD at all — pure static water tiles, ground, walls,
            // bridges); pass 2 draws every subset whose TSD authored a layer
            // 2 (the foam recipes: wheelfallstatic-01, fall-bottom-static,
            // fall-top-static, fall-mist-08x08-static, rvr_static, broken-
            // bridge-fall-*-static). Without the split, an adjacent SNO
            // carrying a plain water tile (rvr_04x08-01 in t_fh00_wfall_1b
            // or rvr-custom-01) iterated later in the placement order
            // depth-fights the foam plane in t_fh00_wheeletc and visually
            // covers the bright foam streaks. Two-pass guarantees foam is
            // last regardless of region-graph placement order.
            for (var pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    // Foam decal pass: push the depth values toward the camera
                    // so a co-planar (or marginally higher-Y) plain-water tile
                    // from a neighboring SNO can't depth-cover the foam plane.
                    // Standard glPolygonOffset trick used for decals; the
                    // offset is in depth-buffer units so it doesn't visibly
                    // shift the geometry. Restored after the pass so other
                    // draws (HUD, particles, actors) see the default state.
                    _gl.Enable(EnableCap.PolygonOffsetFill);
                    _gl.PolygonOffset(-2.0f, -2.0f);
                }
                foreach (var inst in _regionInstances)
                {
                    // SC-FADE-NODES-LNODE — skip snodes that have
                    // any lnode currently fade-hidden by a
                    // trigger_fade_nodes_box (basement/dungeon
                    // reveal). Render is whole-snode-granular; nav
                    // is lnode-granular via NavMesh.FadeHidden.
                    if (_fadedSnodeCounts.ContainsKey(inst.SnodeGuid))
                        continue;
                    // SC-REGION-LAYER-HIDE — DS1 hides the entire upper
                    // region when the player is in a sub-region beneath
                    // it (basement / cellar / cave / dungeon). Test is
                    // region-vs-region, not per-frame Y, so it stays
                    // stable while the player climbs a staircase inside
                    // the lower region — only flips back on region
                    // change.
                    if (IsAbovePlayer(inst.RegionPath)) continue;
                    // SC-PROP-ANIM — nodal tex anim: gizmo-enabled V crawl
                    // on this node's textures (chains, belts). Wraps at 1.0
                    // so the float never loses precision over a long session.
                    _nodalScrollExtra = _nodalTexAnimOn.Count > 0 && _nodalTexAnimOn.Contains(inst.SnodeGuid)
                        ? new Vector2(0f, -(float)(_terrainTime * 0.45 % 1.0))
                        : Vector2.Zero;
                    var resolvedNames = GetResolvedSubsetTexNames(inst.Mesh, inst.TexsetAbbr);
                    bool modelSet = false;
                    for (var i = 0; i < inst.Mesh.Subsets.Count; i++)
                    {
                        bool isFoam = _tsdStore is not null && _tsdStore.Get(resolvedNames[i])?.Layer2 is not null;
                        if ((pass == 0 && isFoam) || (pass == 1 && !isFoam)) continue;
                        if (!modelSet) { _meshShader.SetMatrix4("uModel", inst.World); modelSet = true; }
                        // SC-DEADWIRE F6 — per-instance phase for
                        // timesyncanimation=false recipes.
                        ApplyAnimatedTextureBinding(resolvedNames[i],
                            (inst.SnodeGuid % 977u) * 0.037f);
                        inst.Mesh.DrawSubset(i);
                    }
                }
                if (pass == 1)
                {
                    _gl.PolygonOffset(0f, 0f);
                    _gl.Disable(EnableCap.PolygonOffsetFill);
                }
            }
            _nodalScrollExtra = Vector2.Zero;   // SC-PROP-ANIM — terrain-only
            ResetAnimatedTextureBinding();
            _meshShader.SetInt("uUvOrient", 0);
        }

        // SC-DECALS — projected decal layer (burnt-wood char on the farmhouse doors,
        // blood, ground scorch, drop shadows) over the finished world geometry, before
        // particles + HUD. Depth-tested against terrain + props; manages + restores its
        // own blend/depth-write/polygon-offset state.
        // SC-DECAL-DIAG — F6 toggles the whole decal layer live (dev knob; strip
        // before v1.0). One keypress answers "is that floating thing a decal?".
        if (_decalsVisible) _decalRenderer?.Draw(vp);

        // ALPHA-2V — blob drop-shadows under actors (Options → Video →
        // Shadows: simple_party = party only, complex_party = everyone).
        // Drawn after the world + actors with depth-write off, so actor
        // meshes correctly occlude their own splat. ~90u camera cull.
        if (_shadowMode != "none" && _actors.Count > 0 && _gl is not null)
        {
            _blobShadows ??= new BlobShadowRenderer(_gl);
            _blobShadows.Begin();
            bool partyOnly = _shadowMode == "simple_party";
            var shadowEye = _camera.Position;
            foreach (var a in _actors)
            {
                if (a.Hidden || a.IsDead) continue;
                if (partyOnly && !(a.IsPlayer || a.IsPartyMember)) continue;
                var feet = a.CurrentTransform.Translation;
                if (Vector3.DistanceSquared(feet, shadowEye) > 90f * 90f) continue;
                // Actor scale rides the world matrix's X basis length.
                float sx = new Vector3(a.CurrentTransform.M11,
                                       a.CurrentTransform.M12,
                                       a.CurrentTransform.M13).Length();
                _blobShadows.Add(feet, 0.55f * Math.Clamp(sx, 0.4f, 3f));
            }
            _blobShadows.Draw(vp);
        }

        // SC-MOUSE-FX — selection rings + the fading destination marker,
        // right above the shadows tier so actors still occlude them.
        DrawSelectionWorldFx(vp);

        // Phase 17-SC-E — billboard particles. Sit above the world scene
        // (depth-tested against actors + props) but below the HUD ortho
        // pass so smoke columns get occluded by the farmhouse correctly.
        // Camera basis comes from the view matrix's transpose inside the
        // shader — passing view + proj separately rather than a combined
        // VP keeps that math local.
        if (_particles is not null)
        {
            var pview = _camera.GetView();
            var pproj = _camera.GetProjection(aspect);
            _particles.Draw(pview, pproj, _camera.Position);
        }

        // Phase 15a / Phase 21-SC-INV-A — 2D HUD overlay. Drawn last so it
        // sits over the 3D scene; BeginPass turns off depth + enables alpha
        // blend, EndPass restores both. The previous always-on HUD (200-wide
        // HP/MP bars + XP + Gold lines) is replaced by a small mini-HUD in
        // the top-left (two tiny vials around a portrait stub plus the two
        // active-spell icons) so the three DS1 panels (Character / Inventory
        // / SpellBook) can dock across the top dock row.
        // Phase 21-SC-BARREL-A1 — DS1 sprite cursor: pick state for this
        // frame (priority: enemy > breakable > loot pile > NPC > default)
        // and hide the OS cursor on the first frame our sprite is ready.
        // Cheap per-frame work (a few k-prop scans) and run before the HUD
        // pass so animated states get the same _terrainTime stride the
        // particle systems use.
        UpdateCursorState();
        EnsureOsCursorHidden();

        if (_textRenderer is not null && _textRenderer.HasFont)
        {
            _textRenderer.BeginPass();
            var col   = new Vector4(1f, 1f, 1f, 1f);
            var dim   = new Vector4(0.7f, 0.7f, 0.7f, 1f);
            // Phase 21d-2a-viii-c — hero name banner. Sits top-center so the
            // creator-typed name (or restored save value) is visible without
            // crowding the panel dock row. Skipped when empty (env-var path).
            if (_player is not null && !string.IsNullOrEmpty(_heroName))
            {
                int textW = _textRenderer.MeasureWidth(_heroName);
                int hx = (size.X - textW) / 2;
                _textRenderer.DrawString(size.X, size.Y, _heroName, hx, 12, col);
            }

            // Phase 23-SC-OPTIONS-FOLD2 — Show Framerate top-right. EMA
            // smoothing on dt so the counter doesn't flicker every frame;
            // 1/16 weight is a reasonable balance for a 60fps source.
            // Clamp dt to 100ms before mixing so a multi-second region
            // load (where dt spikes to 1000+ ms) doesn't poison the EMA
            // and leave the counter showing single-digit fps for ~1s
            // after the load completes.
            if (_showFps)
            {
                double sample = Math.Min(dt * 1000.0, 100.0);
                _fpsEmaMs += (sample - _fpsEmaMs) / 16.0;
                int fps = _fpsEmaMs > 0 ? (int)(1000.0 / _fpsEmaMs) : 0;
                var fpsStr = $"{fps} fps";
                int fw = _textRenderer.MeasureWidth(fpsStr);
                _textRenderer.DrawString(size.X, size.Y, fpsStr, size.X - fw - 8, 8, col);
            }

            // SC-RECORD — blinking REC chip, top-right, first 4s only
            // (the capture includes the HUD; a persistent chip would sit
            // in the whole clip). Start/stop/saved receipts go through
            // the message strip like screenshots.
            if (_recorder.IsRecording && _recorder.Elapsed.TotalSeconds < 4.0)
            {
                var e = _recorder.Elapsed;
                var recStr = $"REC {(int)e.TotalMinutes}:{e.Seconds:D2}";
                int recW = _textRenderer.MeasureWidth(recStr);
                int rx = size.X - recW - 8, ry = _showFps ? 26 : 8;
                var red = new Vector4(0.88f, 0.14f, 0.14f, 1f);
                if (e.Milliseconds < 500)
                    _barRenderer?.DrawRect(size.X, size.Y, rx - 14, ry + 2, 9, 9, red);
                _textRenderer.DrawString(size.X, size.Y, recStr, rx, ry, red);
            }

            // Phase 22-AUTH-CHAR-AWP — DS1's always-on player AWP at
            // top-left: HP/MP bars, portrait, 4 weapon/skill slots, and
            // (gas-authored) inventory button. Replaces the home-grown
            // mini-HUD from Phase 21 with the authentic character_awp.gas
            // layout. Drawn at the same always-on z-tier as data_bar +
            // overhead bars; per-panel modals still overlay it.
            DrawCharacterAwp(size.X, size.Y);

            // Phase 27 — member_labels: floating party-member names over
            // their heads when the L "Character Labels" toggle is on
            // (default off — no text over anyone's head unasked). The X
            // "Health/Mana Bars" toggle draws through the authored-art
            // DrawOverheadStatusBars pass below.
            DrawMemberLabels(size.X, size.Y);

            // Phase 22-INFORAIL-B + anchor fold — gas-cited info-rail
            // layout. Per hud_character.gas / hud_inventory.gas /
            // hud_spell.gas: paperdoll 167w, inventory 134w (MAX-mode)
            // or 390w (MIN), spellbook 155w; all 449h.
            //
            // ANCHOR: the rail starts to the RIGHT of the AWP's slot 1
            // (which stays visible during the rail-open transformation).
            // ALPHA-2V RE-BASELINE — the AWP now scales by the same shared
            // clamped baseline as the rail, so the dock chain MUST anchor
            // at the AWP's actual scale (anchoring at raw viewportH/480
            // while the AWP draws clamped strands the rail — and the
            // companion tiles beyond it — far right of the AWP cluster):
            //   paperdoll X = round(AwpAnchorX * awpScale)
            //   inv MAX X   = paperdollX + paperdollW (rail scale)
            //   spellbook X = invX + invW
            float awpScale = Hud.CharacterAwp.Scale(size.Y);
            float infoRailScale = Hud.InfoRailLayout.Scale(size.Y);
            const int AwpAnchorX = 87; // gas-x where the AWP ends + rail begins
            int paperdollX = (int)System.Math.Round(AwpAnchorX * awpScale);
            int paperdollW = (int)System.Math.Round(Hud.InfoRailLayout.Pane1.W * infoRailScale);
            int inventoryMaxX = paperdollX + paperdollW;
            int inventoryMaxW = (int)System.Math.Round(Hud.InfoRailLayout.InventoryMax.W * infoRailScale);
            int spellbookX    = inventoryMaxX + inventoryMaxW;
            int spellbookW    = Hud.SpellBookPanel.WidthAt(size.Y);
            int spellbookH    = Hud.SpellBookPanel.HeightAt(size.Y);
            _ = spellbookW; _ = spellbookH; // (informational; SpellBookPanel.Draw computes its own scale)
            // Min-mode inventory (no paperdoll) sits flush against the
            // AWP cluster the same way paperdoll would in max mode.
            int inventoryMinX = paperdollX;
            const int panelTopY    = 0;

            // SC-HUD-DRAG — info-rail placement. RailLocked (default): the
            // trio moves as ONE unit anchored by PaperdollPos, preserving
            // the DS1 dock chain internally; unlocked: each panel takes its
            // own stored spot. -1 sentinels = the authored chain positions.
            var railPos = _optionsMenu.Live;
            int pdX = railPos.PaperdollPosX >= 0f ? (int)MathF.Round(railPos.PaperdollPosX * size.X) : paperdollX;
            int pdY = railPos.PaperdollPosY >= 0f ? (int)MathF.Round(railPos.PaperdollPosY * size.Y) : panelTopY;
            int invX, invY, spX, spY;
            if (railPos.RailLocked)
            {
                int railDx = pdX - paperdollX, railDy = pdY - panelTopY;
                invX = (_charPanelOpen ? inventoryMaxX : inventoryMinX) + railDx;
                invY = panelTopY + railDy;
                spX  = spellbookX + railDx;
                spY  = panelTopY + railDy;
            }
            else
            {
                invX = railPos.InventoryPosX >= 0f ? (int)MathF.Round(railPos.InventoryPosX * size.X)
                                                   : (_charPanelOpen ? inventoryMaxX : inventoryMinX);
                invY = railPos.InventoryPosY >= 0f ? (int)MathF.Round(railPos.InventoryPosY * size.Y) : panelTopY;
                spX  = railPos.SpellbookPosX >= 0f ? (int)MathF.Round(railPos.SpellbookPosX * size.X) : spellbookX;
                spY  = railPos.SpellbookPosY >= 0f ? (int)MathF.Round(railPos.SpellbookPosY * size.Y) : panelTopY;
            }
            int railH = (int)System.Math.Round(449 * infoRailScale);
            _paperdollRect = (pdX, pdY, paperdollW, railH);

            if (_charPanelOpen && _player is not null)
            {
                // The sheet shows either the player (target 0) or a clicked
                // companion. If the targeted companion has left/died, fall back
                // to the player so the sheet never renders a stale member.
                var sheetMember = _paperdollTargetIndex <= 0
                    ? _player
                    : _party.FirstOrDefault(m => m.PartyIndex == _paperdollTargetIndex);
                if (sheetMember is null) { _paperdollTargetIndex = 0; sheetMember = _player; }
                bool isPlayerSheet = _paperdollTargetIndex <= 0;

                _characterPanel.OriginX = pdX;
                _characterPanel.OriginY = pdY;
                _characterPanel.IsOpen  = true;
                // Armor + weapon stats come from the active character's live equipment.
                int armor = ComputeArmorRating(ActiveEquipment);
                // SC-COMPANION-PROGRESSION — companions read their OWN seeded
                // pool (authored levels + earned XP), same shape as the PC's.
                var sheetProgression = isPlayerSheet
                    ? _progression
                    : _memberProgression.GetValueOrDefault(_paperdollTargetIndex);
                // Phase 21-SC-INV-B — skill XP fraction from whichever pool
                // the sheet is showing.
                float xpFrac = 0f;
                if (sheetProgression is not null)
                {
                    long span = sheetProgression.XpForNextLevel - sheetProgression.XpForCurrentLevel;
                    if (span > 0) xpFrac = (float)sheetProgression.XpIntoCurrentLevel / span;
                }
                string sheetName = isPlayerSheet ? _heroName : ResolveMemberName(sheetMember);
                var sheetAttack = isPlayerSheet ? GetPlayerAttackStats() : sheetMember.Actor.Stats;
                string sheetClassTitle = isPlayerSheet ? _playerStartingClass : ResolveMemberClass(sheetMember);
                var portrait = isPlayerSheet
                    ? (_playerPortraitLive
                       ?? (string.IsNullOrEmpty(_playerPortraitIconName) ? null : TryGetGuiTexture(_playerPortraitIconName)))
                    : ResolveMemberPortrait(sheetMember.Actor.Template);
                // SC-ATTR-XP diag — what the sheet READS, one-shot per value
                // change. Pairs with [attr]'s actor hash: a mismatch means
                // the panel and the progression hold different Actor objects.
                if (isPlayerSheet)
                    BodyDiag($"sheet-int-{(int)sheetMember.Actor.Stats.Intelligence}",
                        $"[sheet] reads int={sheetMember.Actor.Stats.Intelligence:F1} " +
                        $"str={sheetMember.Actor.Stats.Strength:F1} dex={sheetMember.Actor.Stats.Dexterity:F1} " +
                        $"actor#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(sheetMember.Actor):X8}");
                _characterPanel.Draw(_barRenderer!, _textRenderer,
                    size.X, size.Y, sheetName, sheetMember.Actor, sheetProgression,
                    sheetAttack, armor, xpFrac,
                    _iconRenderer, portrait,
                    chromeLookup: TryGetGuiTexture,
                    startingClassTitle: sheetClassTitle,
                    // SC-ATTR-TINT — the hero's live gear/buff attribute
                    // layer (members fold defense only today).
                    attrDelta: isPlayerSheet
                        ? (_appliedEnchant.Str, _appliedEnchant.Dex, _appliedEnchant.Int)
                        : default);

                // INFORAIL-PAPERDOLL — equipment paperdoll under the
                // upper stats panes. Reads gas-cited rects from
                // hud_character.gas via PaperdollPanel. Ghost textures
                // cached by name to avoid per-frame GUI texture lookups;
                // equipped icons aren't wired yet (splinter SC-INFORAIL-
                // EQUIPPED-ICONS) so today every slot shows its ghost.
                if (!_paperdollLoaded)
                {
                    _paperdollLoaded = true;
                    _paperdollBotPaneTex = TryGetGuiTexture("b_gui_ig_mnu_cp_bot_01");
                }
                if (_iconRenderer is not null)
                {
                    _paperdoll.Draw(_iconRenderer, _barRenderer!, _textRenderer,
                        size.X, size.Y, pdX, pdY,
                        _paperdollBotPaneTex,
                        ghostName =>
                        {
                            if (_paperdollGhostCache.TryGetValue(ghostName, out var c)) return c;
                            var t = TryGetGuiTexture(ghostName);
                            _paperdollGhostCache[ghostName] = t;
                            return t;
                        },
                        equippedIconLookup: ResolvePaperdollSlotIcon);

                    // SC-VIEW-ROTATE — while the View button is held, the
                    // live full-body render (at the drag yaw) covers the
                    // paperdoll pane. Only the hero has a live body render;
                    // companion sheets keep the slots.
                    if (_pdViewActive && _paperdollTargetIndex == 0)
                    {
                        RenderPaperdollView();
                        if (_pdViewTex is not null)
                        {
                            float pds = Hud.InfoRailLayout.Scale(size.Y);
                            int vpx = pdX;
                            int vpy = pdY + (int)MathF.Round(228 * pds);
                            int vpw = (int)MathF.Round((254 - 87) * pds);
                            int vph = (int)MathF.Round((446 - 228) * pds);
                            // Square render centered in the pane.
                            int side = Math.Min(vpw, vph);
                            _iconRenderer.DrawIcon(size.X, size.Y, _pdViewTex,
                                vpx + (vpw - side) / 2, vpy + (vph - side) / 2,
                                side, side, Vector4.One);
                            _barRenderer!.DrawBorder(size.X, size.Y,
                                vpx + (vpw - side) / 2, vpy + (vph - side) / 2,
                                side, side, new Vector4(0.55f, 0.52f, 0.45f, 1f));
                        }
                    }
                }

                // INFORAIL-F — vertical "spellbook with I" toggle at gas
                // rect 229,238,250,269 (relative to paperdoll gas origin
                // 87,0). Two strips in b_gui_ig_mnu_minimize-book-up:
                //   show state (rail w/o spellbook): uv 0,0,0.65625,0.484375
                //   hide state (rail w/ spellbook):  uv 0,0.5,0.65625,0.984375
                // Click toggles _spellbookWithI and (per DS1's
                // notify(spell_expand)) opens/closes the spellbook if the
                // rail is currently open.
                if (!_spellbookToggleTexLoaded)
                {
                    _spellbookToggleTexLoaded = true;
                    _spellbookToggleTex = TryGetGuiTexture(Hud.InfoRailLayout.SpellbookToggleTex);
                }
                if (_spellbookToggleTex is not null && _iconRenderer is not null)
                {
                    float irs = Hud.InfoRailLayout.Scale(size.Y);
                    var r = Hud.InfoRailLayout.SpellbookToggle;
                    // SC-HUD-DRAG — ride the live (possibly dragged) sheet.
                    int tx = pdX + (int)System.Math.Round((r.X0 - Hud.InfoRailLayout.Pane1.X0) * irs);
                    int ty = pdY + (int)System.Math.Round(r.Y0 * irs);
                    int tw = (int)System.Math.Round(r.W * irs);
                    int th = (int)System.Math.Round(r.H * irs);
                    var uv = _spellbookWithI
                        ? Hud.InfoRailLayout.SpellbookToggleHideUv.Screen()
                        : Hud.InfoRailLayout.SpellbookToggleShowUv.Screen();
                    _iconRenderer.DrawIcon(size.X, size.Y, _spellbookToggleTex,
                        tx, ty, tw, th, System.Numerics.Vector4.One,
                        uv.u0, uv.v0, uv.u1, uv.v1);
                }
            }
            _companionRowRect = default; // SC-HUD-DRAG — rebuilt below when tiles are open
            if (_inventoryOpen && _barRenderer is not null)
            {
                // INFORAIL-B: max mode (paperdoll open) → x=253; min mode
                // (paperdoll closed) → x=89 per hud_inventory.gas.
                // SC-HUD-DRAG — invX/invY carry the lock/override logic.
                var hudPos = _optionsMenu.Live;
                _inventoryPanel.OriginX     = invX;
                _inventoryPanel.OriginY     = invY;
                _inventoryPanel.DimBackdrop = false;
                _inventoryPanel.Gold        = _progression?.Gold ?? 0;
                // Phase 22-AUTH-INV — DS1-authentic chrome assets loaded
                // here per inventory.gas:
                //   button_arrange    → b_gui_ig_mnu_ip_arrange_up
                //   window_gold_bg    → b_gui_ig_mnu_ip_gold_box
                //   window_gold_icon  → b_gui_ig_mnu_ip_gold
                //   button_inventory_exit common_template=x → resolved via
                //                       GetCommonTexture("button_x_up")
                //   gridbox_13x4      → b_gui_ig_mnu_ip_grid
                //   dialog_box_inv_bg common_template=cpbox → via NinePatch
                // The minimize close icon kept as legacy fallback in case
                // the cpbox X button is unresolved on first-frame.
                var invClose   = TryGetGuiTexture("b_gui_ig_mnu_minimize-up");
                var goldCoin   = TryGetGuiTexture("b_gui_ig_mnu_ip_gold");
                // SC-INV-ARRANGE — authored loadtexture swaps: _dwn while the
                // button is held, _hov under the cursor, _up otherwise.
                var arrangeUp  = TryGetGuiTexture(
                    _invArrangePressed ? "b_gui_ig_mnu_ip_arrange_dwn" :
                    _invArrangeHovered ? "b_gui_ig_mnu_ip_arrange_hov" :
                                         "b_gui_ig_mnu_ip_arrange_up")
                    ?? TryGetGuiTexture("b_gui_ig_mnu_ip_arrange_up");
                var goldBg     = TryGetGuiTexture("b_gui_ig_mnu_ip_gold_box");
                var gridTile   = TryGetGuiTexture("b_gui_ig_mnu_ip_grid");
                // The main panel shows the ACTIVE character's backpack + face
                // (player, or a companion whose sheet was opened by clicking their
                // portrait). Gold is party-wide, so it always shows the shared purse.
                // SC-INV-AUTHENTIC-HEADER — DS1's single inventory has NO
                // header portrait (the party rail carries the faces); the
                // header slot belongs to button_arrange. Companion views
                // keep the face there per multi_portrait_human_N (which
                // replaces arrange on those panels).
                var activePortrait = _paperdollTargetIndex <= 0
                    ? null
                    : (_party.FirstOrDefault(m => m.PartyIndex == _paperdollTargetIndex) is { } mem
                        ? ResolveMemberPortrait(mem.Actor.Template) : null);
                _inventoryPanel.Draw(_barRenderer, _textRenderer, _iconRenderer,
                    size.X, size.Y, ActiveInventory, TryGetItemIcon, TryGetItemGridSize,
                    invClose, goldCoin,
                    resolveCommonChrome: GetCommonTexture,
                    arrangeUp: arrangeUp,
                    goldBg: goldBg,
                    gridTile: gridTile,
                    headerPortrait: activePortrait);

                // SC-TOOLTIP-LAYER — the backpack item card moved to
                // DrawHoverTooltips at the END of the HUD pass; drawn here
                // it sat UNDER the companion tiles and later panels.

                // Multi-inventory — tile each open companion's panel to the right
                // of the player's, one panel-width apart (DS1 multi_inventory.gas
                // reuses the same 134-wide inventory panel per member). Each shows
                // that companion's own backpack; gold is player-only in DS1.
                if (_openInventoryMembers.Count > 0)
                {
                    int stride = InventoryPanel.PanelWidth(size.Y);
                    int spellW = Hud.SpellBookPanel.WidthAt(size.Y);
                    // SC-HUD-DRAG — the row of companion tiles moves as ONE
                    // piece: a dragged base replaces "right of the player's
                    // panel"; tiles still stride rightward from the base.
                    int compBaseX = hudPos.CompanionInvPosX >= 0f
                        ? (int)MathF.Round(hudPos.CompanionInvPosX * size.X)
                        : _inventoryPanel.OriginX + stride;
                    int compBaseY = hudPos.CompanionInvPosY >= 0f
                        ? (int)MathF.Round(hudPos.CompanionInvPosY * size.Y)
                        : panelTopY;
                    // SC-COMPANION-SPELLBOOK — a member who has been given a
                    // spell book gets their own spell panel tiled directly
                    // right of their bag, so their slot is bag + book wide.
                    // Publish the per-tile layout for the input routers.
                    _companionTileLayout.Clear();
                    int rowW = 0;
                    foreach (var pidx in _openInventoryMembers.OrderBy(x => x))
                    {
                        bool hasBook = MemberHasSpellbook(pidx);
                        _companionTileLayout.Add((pidx, compBaseX + rowW, hasBook));
                        rowW += stride + (hasBook ? spellW : 0);
                    }
                    _companionRowRect = (compBaseX, compBaseY, rowW,
                        InventoryPanel.PanelHeight(size.Y));
                    foreach (var (pidx, tileX, hasBook) in _companionTileLayout)
                    {
                        // That companion's own face in the header, so the player
                        // can tell whose backpack each tiled panel is.
                        var member = _party.FirstOrDefault(mm => mm.PartyIndex == pidx);
                        var memberPortrait = member is not null
                            ? ResolveMemberPortrait(member.Actor.Template) : null;
                        _companionInvPanel.OriginX     = tileX;
                        _companionInvPanel.OriginY     = compBaseY;
                        _companionInvPanel.DimBackdrop = false;
                        _companionInvPanel.Gold        = 0;
                        _companionInvPanel.Draw(_barRenderer, _textRenderer!, _iconRenderer,
                            size.X, size.Y, GetMemberInventory(pidx), TryGetItemIcon, TryGetItemGridSize,
                            invClose, goldCoin,
                            resolveCommonChrome: GetCommonTexture,
                            arrangeUp: arrangeUp, goldBg: goldBg, gridTile: gridTile,
                            headerPortrait: memberPortrait);
                        if (hasBook)
                        {
                            var book = BookFor(pidx);
                            var spellClose2 = GetCommonTexture("button_x_up")
                                              ?? TryGetGuiTexture("b_gui_cmn_button_x_up");
                            _companionSpellPanel.OriginX = tileX + stride;
                            _companionSpellPanel.OriginY = compBaseY;
                            _companionSpellPanel.IsOpen  = true;
                            _companionSpellPanel.Draw(_barRenderer, _textRenderer!,
                                size.X, size.Y, book?.Primary, book?.Secondary, book?.Placed,
                                _iconRenderer, spellClose2, ResolveSpellInventoryIcon,
                                resolveCommonChrome: GetCommonTexture,
                                rowBox: TryGetGuiTexture("b_gui_ig_mnu_sb_box"));
                        }
                    }
                }
                else
                {
                    _companionTileLayout.Clear();
                }
            }
            if (_spellBookOpen && _barRenderer is not null)
            {
                _spellBookPanel.OriginX = spX;
                _spellBookPanel.OriginY = spY;
                _spellBookPanel.IsOpen  = true;
                // Phase 21-SC-SCROLL-C-1 — the 10 below-active rows are now
                // backed by PlayerSpellbook.Placed. Null entries render as
                // empty cells; the scroll-drag flow (B/C-2) populates them.
                IReadOnlyList<SiegeFX.Core.Assets.SpellTemplate?> placed =
                    _playerSpellbook?.Placed ?? System.Array.Empty<SiegeFX.Core.Assets.SpellTemplate?>();
                // INFORAIL-SPELLBOOK-CHROME — close uses common X button
                // (gas hud_spell.gas:12 texture=b_gui_cmn_button_x_up,
                // common_template=x). Matches the inventory's close X.
                var spellClose = GetCommonTexture("button_x_up")
                                 ?? TryGetGuiTexture("b_gui_cmn_button_x_up");
                // SC-SPELLBOOK-AUTHENTIC — cpbox smoke + sb_box row bands
                // per spell.gas; see the panel for the authored rect table.
                _spellBookPanel.Draw(_barRenderer, _textRenderer!,
                    size.X, size.Y, _playerSpellbook?.Primary, _playerSpellbook?.Secondary, placed,
                    _iconRenderer, spellClose, ResolveSpellInventoryIcon,
                    resolveCommonChrome: GetCommonTexture,
                    rowBox: TryGetGuiTexture("b_gui_ig_mnu_sb_box"));
            }

            // SC-MSG-STRIP — DS1's top-center message strip (ref text.bmp /
            // sparkles.bmp): ONE wide translucent shadowbox band (~65% of the
            // screen width, centered) behind ALL the live lines — the "easier
            // to read" backdrop — with the lines LEFT-ALIGNED inside it,
            // parchment ink, stacked newest-last. Carries screenshot
            // confirmations, skill/attribute level-ups, and quest notices.
            if (_gameMessages.Count > 0 && _barRenderer is not null)
            {
                float hs = Hud.HudScale.Hud(size.Y);
                int fscale = Math.Max(1, (int)MathF.Round(hs));
                int lineH = _textRenderer!.LineHeight * fscale + 2 * fscale;
                int padX = 8 * fscale, padY = 3 * fscale;
                int bandW = (int)(size.X * 0.655f);
                int bandX = (size.X - bandW) / 2;
                int bandY = 0;
                // Sit below the save toast when one is up (same slot in DS1).
                if (_saveToastRemaining > 0f)
                    bandY += _textRenderer.LineHeight * fscale + (int)MathF.Round(24 * hs);
                int bandH = _gameMessages.Count * lineH + padY * 2;
                // Band alpha follows the LONGEST-lived line so the box holds
                // while any line is still readable, then fades with the last.
                float bandA = 0f;
                foreach (var msg in _gameMessages)
                    bandA = MathF.Max(bandA, MathF.Min(1f, msg.Remaining / 0.8f));
                _barRenderer.DrawRect(size.X, size.Y, bandX, bandY, bandW, bandH,
                    new Vector4(0.02f, 0.02f, 0.03f, 0.60f * bandA));
                int lineY = bandY + padY;
                foreach (var msg in _gameMessages)
                {
                    float a = MathF.Min(1f, msg.Remaining / 0.8f);
                    _textRenderer.DrawString(size.X, size.Y, msg.Text, bandX + padX, lineY,
                        new Vector4(0.93f, 0.90f, 0.74f, a), fscale);
                    lineY += lineH;
                }
            }

            // SC-KEY-AUDIT — the chat-history scrollback panel (PageUp opens
            // and pages older lines; PageDown pages back down and closes at
            // the bottom; End clears). Draws under the live strip.
            if (_msgHistoryOpen && _gameMsgLog.Count > 0 && _barRenderer is not null)
            {
                float hs2 = Hud.HudScale.Hud(size.Y);
                int fs2 = Math.Max(1, (int)MathF.Round(hs2));
                int lh2 = _textRenderer!.LineHeight * fs2 + 2 * fs2;
                int pw = (int)(size.X * 0.655f);
                int px2 = (size.X - pw) / 2;
                int rows = Math.Min(MsgHistoryVisible, _gameMsgLog.Count);
                int ph = rows * lh2 + 10 * fs2 + lh2;
                int py2 = (int)MathF.Round(30 * hs2);
                _barRenderer.DrawRect(size.X, size.Y, px2, py2, pw, ph,
                    new Vector4(0.02f, 0.02f, 0.03f, 0.82f));
                _barRenderer.DrawBorder(size.X, size.Y, px2, py2, pw, ph,
                    new Vector4(0.45f, 0.42f, 0.34f, 0.9f));
                int maxScroll2 = Math.Max(0, _gameMsgLog.Count - rows);
                _msgHistoryScroll = Math.Clamp(_msgHistoryScroll, 0, maxScroll2);
                int first = _gameMsgLog.Count - rows - _msgHistoryScroll;
                int ly = py2 + 5 * fs2;
                _textRenderer.DrawString(size.X, size.Y,
                    $"Message History ({_gameMsgLog.Count}){(_gameMsgLocked ? "  [LOCKED]" : "")}",
                    px2 + 8 * fs2, ly, new Vector4(0.75f, 0.70f, 0.55f, 1f), fs2);
                ly += lh2;
                for (int i = 0; i < rows; i++)
                {
                    _textRenderer.DrawString(size.X, size.Y, _gameMsgLog[first + i],
                        px2 + 8 * fs2, ly, new Vector4(0.93f, 0.90f, 0.74f, 1f), fs2);
                    ly += lh2;
                }
            }

            // SC-CHAPTER-TITLE — the authored chapter card art (b_gui_nis_ch0N,
            // ref chapter text.bmp). Full-strength on arrival, then a slow
            // linear fade over the last ChapterTitleFadeSeconds. Sized from
            // the authored 256px window on the 640×480 UI plane, centered at
            // the authored spot (~screen center).
            if (_chapterTitleRemaining > 0f && _chapterCardTex is not null
                && _iconRenderer is not null)
            {
                float a = MathF.Min(1f, _chapterTitleRemaining / ChapterTitleFadeSeconds);
                // Scale with the SHARED clamped HUD scale (BaseMax cap + the
                // user's UI-Scale knob) — raw vh/480 blew the 256px art up
                // fuzzy on 1440p/4K and ignored the knob every other HUD
                // element honors.
                float hs = Hud.HudScale.Hud(size.Y);
                int side = (int)MathF.Round(256f * hs);
                int cx = (size.X - side) / 2;
                int cy = (int)MathF.Round(size.Y * (239f / 480f)) - side / 2;
                _iconRenderer.DrawIcon(size.X, size.Y, _chapterCardTex, cx, cy, side, side,
                    new Vector4(1f, 1f, 1f, a));
            }

            // DS1 save-confirmation banner — a dark status bar near the top
            // ("Game 'X' saved successfully."), the same slot DS1 uses for
            // journal notices. Times out with a short fade; independent of
            // _player so it also shows on a viewer-mode save.
            if (_saveToastRemaining > 0f && _barRenderer is not null && _saveToastText.Length > 0)
            {
                float hs = Hud.HudScale.Hud(size.Y);
                int fscale = Math.Max(1, (int)MathF.Round(hs));
                int tw = _textRenderer!.MeasureWidth(_saveToastText, fscale);
                int th = _textRenderer.LineHeight * fscale;
                int padX = 18 * fscale, padY = 5 * fscale;
                int boxW = tw + padX * 2, boxH = th + padY * 2;
                int boxX = (size.X - boxW) / 2;
                int boxY = (int)MathF.Round(12 * hs);
                float a = MathF.Min(1f, _saveToastRemaining / 0.8f); // fade last 0.8s
                _barRenderer.DrawRect (size.X, size.Y, boxX, boxY, boxW, boxH, new Vector4(0.03f, 0.03f, 0.04f, 0.82f * a));
                _barRenderer.DrawBorder(size.X, size.Y, boxX, boxY, boxW, boxH, new Vector4(0.45f, 0.40f, 0.28f, a));
                _textRenderer.DrawString(size.X, size.Y, _saveToastText, boxX + padX, boxY + padY,
                                         new Vector4(0.95f, 0.90f, 0.72f, a), fscale);
            }

            // Phase 22-A SC-HUD-DATABAR — DS1's always-on bottom-row HUD
            // button strip. Drawn at the same z-tier as the mini-HUD vials
            // so both share the always-on layer; modal panels below this
            // continue to overlay it. Tick down the quest-indicator flash
            // each frame.
            if (_questIndicatorFlashRemaining > 0f)
                _questIndicatorFlashRemaining -= (float)dt;
            DrawDataBar(size.X, size.Y);
            // Phase 22-H SC-HUD-OVERHEAD-BARS — DS1-authentic floating
            // HP/MP bars above every visible combatant's head. Needs the
            // camera's view-projection matrix to project worldPos to NDC.
            DrawOverheadStatusBars(size.X, size.Y, vp);
            // SC-QUEST-UI-B — always-visible tracker for the current
            // objective; the full journal stays on 'L'.
            DrawQuestTracker(size.X, size.Y);
            // SC-COMPASS — hidden during cinematics like the rest of the HUD.
            if (_nisPhase == NisPhase.Off) DrawCompass(size.X, size.Y);
            // SC-WEATHER-E — lightning screen flash washes the frame for the
            // strike envelope (~0.45s double pulse). Drawn over the HUD: a
            // real strike whites out everything, and the wash is brief.
            DrawWeatherFlash(size.X, size.Y);
            // SC-MEGAMAP-CHROME — the authored parchment border frames the
            // overhead view while the TAB map is up.
            if (_megaMapActive) DrawMegaMapChrome(size.X, size.Y);
            // SC-NIS-VERBS — fader_proxy screen fade over the world, under
            // the letterbox/subtitles so NIS text stays readable mid-fade.
            if (_screenFadeAlpha > 0.01f && _barRenderer is not null)
                _barRenderer.DrawRect(size.X, size.Y, 0, 0, size.X, size.Y,
                    new Vector4(0f, 0f, 0f, _screenFadeAlpha));
            DrawNisLetterbox(size.X, size.Y);
            DrawSubtitles(size.X, size.Y);

            // v12 Load-window preview — capture a downscaled screenshot off the
            // freshly-composited world+HUD (before any modal draws below) at a
            // throttled ~1 Hz, and only on clean frames (no modal / not at the
            // frontend). The most recent capture rides the next save so the Load
            // window can show a real gameplay thumbnail rather than a shot of the
            // Save dialog. glReadPixels stalls the pipe, hence the throttle.
            if (_thumbRefreshTimer > 0f) _thumbRefreshTimer -= (float)dt;
            if (_thumbRefreshTimer <= 0f && _player is not null && !_bootMode
                && !_pauseMenu.IsOpen && !_saveDialog.IsOpen && !_loadDialog.IsOpen
                && !_handbook.IsOpen && !_optionsMenu.IsOpen && !_questLogOpen
                && !_inventoryOpen && !_dialogue.IsOpen && !_vendor.IsOpen
                && _nisPhase == NisPhase.Off)
            {
                RefreshSceneThumbnail(size.X, size.Y);
                _thumbRefreshTimer = ThumbInterval;
            }

            // Phase 21-SC-INV-A — the grid inventory was relocated into the
            // top-dock row above (alongside the character + spell book panes)
            // so the previous centered/modal draw call for it is gone here.
            // Phase 20b — quest log overlay (toggled by 'L'). Sits at the same
            // z-tier as the inventory; both can be open without conflict but
            // visually overlap if so. The pause menu still draws on top.
            if (_questLogOpen && _barRenderer is not null && _progression is not null)
            {
                _questLogPanel.Draw(_barRenderer, _textRenderer!, _iconRenderer,
                                    size.X, size.Y, _progression.Journal, TryGetGuiTexture);
            }
            // Phase 20a: dialogue panel. Sits above the inventory but under the
            // pause menu, so pressing Esc while talking still surfaces the pause
            // overlay if the dialogue close-on-Esc somehow misses.
            if (_dialogue.IsOpen && _barRenderer is not null)
            {
                _dialogue.Draw(_barRenderer, _textRenderer!, _iconRenderer,
                               TryGetGuiTexture, size.X, size.Y);
            }
            // Phase 20d: vendor trade overlay. Same z-tier as the dialogue —
            // they're mutually exclusive in practice (vendor only opens once
            // dialogue closes) but the explicit draw keeps both branches local.
            if (_vendor.IsOpen && _barRenderer is not null)
            {
                _vendor.Draw(_barRenderer, _textRenderer!, _iconRenderer,
                             TryGetGuiTexture, TryGetItemIcon,
                             size.X, size.Y, _progression?.Gold ?? 0);
            }
            // Phase 15d: pause menu (Esc). Drawn after the inventory so its
            // backdrop dims the inventory grid too — pause is the topmost UI.
            if (_pauseMenu.IsOpen && _barRenderer is not null)
            {
                _pauseMenu.Draw(_barRenderer, _textRenderer!, _iconRenderer, TryGetGuiTexture, size.X, size.Y);
            }
            // DS1 Save Game window + Adventurer's Handbook — self-contained
            // modals opened from the pause menu / F12. Drawn above pause so
            // either owns the screen; both tick their own blink/animation off
            // render dt (gameplay dt is frozen while they're up).
            if (_saveDialog.IsOpen && _barRenderer is not null && _gl is not null)
            {
                _saveDialog.Tick((float)dt);
                _saveDialog.Draw(_gl, _barRenderer, _textRenderer!, _iconRenderer,
                                 TryGetGuiTexture, GetCommonTexture, size.X, size.Y);
            }
            // DS1 Load Game window (in-game pose). The frontend pose is drawn
            // over the boot chrome instead (see DrawBootScene). Needs _gl to
            // upload the preview thumbnail.
            if (_loadDialog.IsOpen && !_loadDialog.MainMenuStyle && _barRenderer is not null && _gl is not null)
            {
                _loadDialog.Draw(_gl, _barRenderer, _textRenderer!, _iconRenderer,
                                 TryGetGuiTexture, GetCommonTexture, size.X, size.Y);
            }
            if (_handbook.IsOpen && _barRenderer is not null)
            {
                _handbook.Draw(_barRenderer, _textRenderer!, _iconRenderer,
                               TryGetGuiTexture, GetCommonTexture, size.X, size.Y);
            }
            // SC-MOUSE-FX — the drag marquee + bottom-center hover status line.
            DrawMouseHudFx(size.X, size.Y);
            // SC-DEFEAT — dim + Defeat! + the load-question dialog, above the
            // HUD but below the Load window / dev console.
            DrawDefeatOverlay(size.X, size.Y);
            // SC-ENDGAME — the victory card stacks the same way.
            DrawVictoryOverlay(size.X, size.Y);
            // SC-DEVMODE — dev console overlay (topmost among gameplay HUD).
            DrawDevConsole(size.X, size.Y);
            // Phase 23-SC-OPTIONS-A: options dialog. Drawn after pause
            // menu so a future "open Options from pause" hookup stacks
            // visually correctly (Options on top of Pause's dim layer).
            if (_optionsMenu.IsOpen && _barRenderer is not null)
            {
                _optionsMenu.Draw(_barRenderer, _textRenderer!, _iconRenderer, _frontendScene, size.X, size.Y,
                    commonChrome: GetCommonTexture);
            }
            // Phase 21d-2a-viii-b: character creator. Topmost UI when open
            // (gates player spawn). Sits above pause because Esc-while-creator
            // is meaningless; the panel owns the screen until Begin/Cancel.
            // Phase 29-CD-CREATOR-FIX — skip this in-region block when the
            // FrontendScene is already in CharacterSelect (boot path); the
            // boot chrome render block already drew the full chrome + preview
            // + per-button overlays, and re-running here would paint an
            // opaque black backdrop over the pillars and chrome.
            bool inBootCreatorPath = _bootMode && _frontendScene is not null
                && _frontendScene.State == Hud.FrontendScene.ScreenState.CharacterSelect;
            if (_creator.IsOpen && _barRenderer is not null && !inBootCreatorPath)
            {
                // Phase 21d-2a-viii-FE — render the full DS1 frontend scene
                // (8 layered ASPs, cd-state PRS pose). Lazy-loaded on first
                // open (Objects.dsres handle isn't ready until LoadPlayActors
                // wires _playResolver). Falls back to the BarRenderer
                // scaffolded card if any of the meshes / clips fail to load
                // so the user is never staring at a blank screen during dev.
                EnsureFrontendScene();
                if (_frontendScene is not null)
                {
                    // Modal backdrop — DS1's frontend is fullscreen with no
                    // gameplay behind it, but our creator opens against an
                    // already-rendered region (so the play camera can come
                    // back instantly on Cancel). Fill the viewport opaque
                    // before the chrome so the world doesn't bleed through
                    // gaps in the layered ASPs (backdrop is 9-cell stained
                    // glass with transparency between cells; leftside /
                    // rightside don't reach the screen edges; etc).
                    _barRenderer.DrawRect(size.X, size.Y, 0, 0, size.X, size.Y,
                        new Vector4(0f, 0f, 0f, 1f));
                    _frontendScene.Tick((float)dt);
                    _frontendScene.Draw(size.X, size.Y);

                    // Phase 21d-2a-viii-FE-2 — live hero preview into the
                    // listener rect. Lazy-built on first cd-state frame
                    // (needs _templateStore + _playResolver + _skinShader,
                    // all populated by LoadPlayActors). Renders BEFORE the
                    // text overlay so typed name + axis labels read on top
                    // of the rotating model.
                    if (_heroPreview is null && _gl is not null && _skinShader is not null
                        && _templateStore is not null && _playResolver is not null)
                    {
                        _heroPreview = new HeroPreviewRenderer(
                            _gl, _skinShader, _templateStore, _playResolver,
                            ResolveActorTexture);
                    }
                    if (_heroPreview is not null)
                    {
                        _heroPreview.EnsurePreview(_creator.Picker);
                        _heroPreview.Tick((float)dt);
                        _heroPreview.Draw(size.X, size.Y);
                    }

                    // Phase 29-CD-CREATOR — per-button asp render for the
                    // 12 spinner widgets (gender / head / face / hair /
                    // shirt / pants × L/R) via art_mapping.gas, plus
                    // typed-name overlay via TextRenderer.
                    DrawCharacterCreatorOverlays(size.X, size.Y);
                }
            }
            // Phase 20c — quest goal marker. Picks the first active quest with
            // an unmet kill objective, finds the nearest live target actor whose
            // template name contains the goal template substring, and either
            // drops a chevron over its head (on-screen) or clamps a chevron to
            // the viewport edge pointing toward it (off-screen). Drawn before
            // floating text so combat numerics layer on top — the marker is
            // ambient, the float is feedback.
            DrawQuestGoalMarker(size.X, size.Y, vp);

            // Phase 9-SC-LL — world-space item-name labels above loot piles.
            // Drawn before the floating cast labels so a damage popup can
            // briefly stack on top of a label when both occupy the same area.
            // Gold by default, red when the mouse pointer is over the label
            // rect — matches DS1's hover behavior. Backdrop is a thin dark
            // panel for legibility against busy terrain.
            // INFORAIL fold — gas data_bar's window_labels checkbox toggles
            // _overheadLabelsVisible (line 8932); skip the whole label
            // render when the user has labels hidden. Alt key + the
            // bottom-right labels button both flip this flag, matching
            // DS1's notify(labels_on)/notify(labels_off) behavior.
            if (_frameLootLabels.Count > 0 && _barRenderer is not null
                && _overheadLabelsVisible && _nisPhase == NisPhase.Off)
            {
                int mx = -1, my = -1;
                if (_input is not null && _input.Mice.Count > 0)
                {
                    var mp = _input.Mice[0].Position;
                    mx = (int)mp.X;
                    my = (int)mp.Y;
                }
                // DS1 ground-loot labels use the same #AAA78E body font as the
                // top-dock panels; hover bumps to red so the cursor target still pops.
                // Phase 21-SC-INV-A2 (round 9) — bg pushed to near-opaque +
                // light-grey border so the tag reads as a proper chrome
                // panel against grass; before this round the 0.78-alpha
                // dark fill paired with the muted ink disappeared into busy
                // terrain (user reported "lost the label boxes").
                var gold      = new Vector4(0.667f, 0.655f, 0.557f, 1f);
                var red       = new Vector4(1.00f, 0.28f, 0.20f, 1f);
                var bg        = new Vector4(0.06f, 0.05f, 0.04f, 0.92f);
                var labelEdge = new Vector4(0.55f, 0.57f, 0.60f, 1f);
                foreach (var (world, label) in _frameLootLabels)
                {
                    var wp4 = new Vector4(world, 1f);
                    var clip = Vector4.Transform(wp4, vp);
                    if (clip.W <= 0.001f) continue;
                    float ndcX = clip.X / clip.W;
                    float ndcY = clip.Y / clip.W;
                    if (ndcX < -1.2f || ndcX > 1.2f || ndcY < -1.2f || ndcY > 1.2f) continue;
                    int sx = (int)((ndcX * 0.5f + 0.5f) * size.X);
                    int sy = (int)((1f - (ndcY * 0.5f + 0.5f)) * size.Y);
                    int textW = _textRenderer!.MeasureWidth(label);
                    int textH = _textRenderer.Font?.Height ?? 14;
                    int padX = 4, padY = 2;
                    int boxW = textW + padX * 2;
                    int boxH = textH + padY * 2;
                    int boxX = sx - boxW / 2;
                    int boxY = sy - boxH;
                    bool hover = mx >= boxX && mx < boxX + boxW
                              && my >= boxY && my < boxY + boxH;
                    _barRenderer.DrawRect  (size.X, size.Y, boxX, boxY, boxW, boxH, bg);
                    _barRenderer.DrawBorder(size.X, size.Y, boxX, boxY, boxW, boxH, labelEdge);
                    _textRenderer.DrawString(size.X, size.Y, label,
                        boxX + padX, boxY + padY, hover ? red : gold);
                }
            }

            // Phase 17a — floating cast-feedback labels. World-anchored, so we
            // project each WorldPos through the same VP used for the 3D pass.
            // Behind the camera (clip-space w<=0) entries skip silently.
            if (_floatingTexts.Count > 0)
            {
                foreach (var ft in _floatingTexts)
                {
                    var wp = new Vector4(ft.WorldPos, 1f);
                    var clip = Vector4.Transform(wp, vp);
                    if (clip.W <= 0.001f) continue;
                    float ndcXX = clip.X / clip.W;
                    float ndcYY = clip.Y / clip.W;
                    int sx = (int)((ndcXX * 0.5f + 0.5f) * size.X);
                    int sy = (int)((1f - (ndcYY * 0.5f + 0.5f)) * size.Y);
                    // Lifecycle: rise ~12px over its life and fade alpha in
                    // the last third — float-up popup behavior.
                    float t01 = 1f - (ft.Remaining / MathF.Max(0.0001f, ft.Total));
                    sy -= (int)(t01 * 12f);
                    float a = ft.Remaining > ft.Total * 0.33f
                        ? 1f
                        : ft.Remaining / (ft.Total * 0.33f);
                    var c = ft.Color; c.W *= a;
                    int textW = ft.Text.Length * 7;
                    _textRenderer!.DrawString(size.X, size.Y, ft.Text, sx - textW / 2, sy, c);
                }
            }
            // Phase 21-SC-SCROLL-PRE-1 — cursor-anchored spell-scroll overlay.
            // Drawn LAST in the HUD pass so it stacks above every panel,
            // matching the DS1 behavior where a held scroll occludes any
            // window the cursor is over. No-op when no drag is in flight
            // (the `_cursorScroll != null` gate). Uses the spell's
            // authored `inventory_icon` from the larger `_inv` icon set
            // (b_gui_ig_i_ic_sp_*_inv) for DS1-faithful art.
            //
            // Invariant: this block must run while `_textRenderer.BeginPass`
            // is still active (the inner `if (_textRenderer is not null)`
            // higher up wraps the whole HUD pass through to the EndPass on
            // the line below). IconRenderer.DrawIcon requires an active
            // blend pass; if a future refactor moves EndPass earlier this
            // block needs its own pass scope.
            if (_cursorScroll is not null && _iconRenderer is not null)
            {
                var iconTex = ResolveSpellInventoryIcon(_cursorScroll);
                if (iconTex is not null)
                {
                    const int iconSize = 32;
                    int cx = (int)_currentMousePos.X - iconSize / 2;
                    int cy = (int)_currentMousePos.Y - iconSize / 2;
                    _iconRenderer.DrawIcon(size.X, size.Y, iconTex,
                        cx, cy, iconSize, iconSize, Vector4.One);
                }
            }
            // INFORAIL-PAPERDOLL-INTERACT — generic cursor item icon.
            // Audit fold: when the item template lacks a [gui]inventory_
            // _icon (TryGetItemIcon returns null), draw a placeholder
            // rect so the player sees they're carrying SOMETHING. An
            // invisible cursor-item == silently lost item.
            else if (_cursorItem is not null && _iconRenderer is not null)
            {
                const int iconSize = 32;
                int cx = (int)_currentMousePos.X - iconSize / 2;
                int cy = (int)_currentMousePos.Y - iconSize / 2;
                if (_cursorItemIcon is not null)
                {
                    _iconRenderer.DrawIcon(size.X, size.Y, _cursorItemIcon,
                        cx, cy, iconSize, iconSize, Vector4.One);
                }
                else if (_barRenderer is not null)
                {
                    var bg     = new Vector4(0.08f, 0.08f, 0.10f, 0.85f);
                    var border = new Vector4(0.86f, 0.83f, 0.69f, 1f);
                    _barRenderer.DrawRect  (size.X, size.Y, cx, cy, iconSize, iconSize, bg);
                    _barRenderer.DrawBorder(size.X, size.Y, cx, cy, iconSize, iconSize, border);
                    _textRenderer!.DrawString(size.X, size.Y, "?",
                        cx + iconSize / 2 - 3, cy + iconSize / 2 - 4, border);
                }
            }
            // Phase 21-SC-BARREL-A1 — DS1 sprite cursor (sword / red sword /
            // hammer / hand / talk). Drawn after every other HUD element so
            // it stacks above panels (matches DS1 layering). Suppressed when
            // a scroll is mid-drag — the scroll overlay above replaces the
            // cursor — and when RMB camera-look has the OS cursor in Raw
            // mode. UpdateCursorState ran once per frame just below the
            // scene draw; ResolveCursorVisual maps the state to a texture
            // and authored hotspot.
            else if (_iconRenderer is not null && !_mouseLookActive && _player is not null)
            {
                var (cTex, hsx, hsy, sz) = ResolveCursorVisual();
                if (cTex is not null)
                {
                    int cx = (int)_currentMousePos.X - hsx;
                    int cy = (int)_currentMousePos.Y - hsy;
                    _iconRenderer.DrawIcon(size.X, size.Y, cTex, cx, cy, sz, sz, Vector4.One);
                }
            }
            // SC-TOOLTIP-AUTH/SC-TOOLTIP-LAYER — the retail help layer:
            // EVERY hover stat card (spellbook rows, backpack items,
            // equipped paperdoll slots), then the bottom-center strip
            // (subject + authored education line) for whatever the cursor
            // is over. Drawn last so nothing paints across them.
            if (_player is not null)
            {
                DrawHoverTooltips(size.X, size.Y);
                DrawBottomHelpStrip(size.X, size.Y);
            }
            _textRenderer!.EndPass();
        }
        // SC-SCREENSHOT — grab the completed back buffer as the very last
        // act of the frame, so the capture contains everything (HUD included)
        // exactly as presented.
        CaptureScreenshotIfPending();

        // SC-RECORD — recorder status lines are produced on encoder
        // threads; surface them on the strip from the render thread.
        while (_recorder.StatusLines.TryDequeue(out var recLine))
            AddGameMessage(recLine);
        while (_renderThreadWork.TryDequeue(out var work))
            work();
    }

    /// <summary>Draws a single HP/MP-style bar at (<paramref name="x"/>,<paramref name="y"/>):
    /// dark background, fill scaled to current/max, 1-pixel border, and a label like
    /// "HP 38/50" laid alongside via <see cref="TextRenderer"/>.</summary>
    private void DrawHudBar(int viewportW, int viewportH, int x, int y, int w, int h,
                            float current, float max, Vector4 fillColor, string label)
    {
        if (_barRenderer is null || _textRenderer is null) return;
        if (max <= 0f) return;
        float pct = Math.Clamp(current / max, 0f, 1f);
        int fillW = (int)MathF.Round(pct * (w - 2));
        var bg     = new Vector4(0.10f, 0.10f, 0.10f, 0.82f);
        var border = new Vector4(0.85f, 0.85f, 0.85f, 0.85f);
        var text   = new Vector4(1f, 1f, 1f, 1f);

        _barRenderer.DrawRect(viewportW, viewportH, x, y, w, h, bg);
        if (fillW > 0)
            _barRenderer.DrawRect(viewportW, viewportH, x + 1, y + 1, fillW, h - 2, fillColor);
        _barRenderer.DrawBorder(viewportW, viewportH, x, y, w, h, border);

        var caption = $"{label} {(int)MathF.Round(current)}/{(int)MathF.Round(max)}";
        _textRenderer.DrawString(viewportW, viewportH, caption, x + w + 8, y - 1, text);
    }

    /// <summary>Phase 21-SC-INV-A2 — three chevrons inside the open-panels
    /// strip. Drawn as five 1px stair-step rects per chevron (the BarRenderer
    /// is rect-only). <paramref name="pointRight"/> flips the stair-step so
    /// the same call site can show "click to open" (left) when panels are
    /// closed and "click to close" (right) when they're open. Phase 21-SC-INV-B
    /// (round 4): per-pixel brightness graduates from the chevron tip (full
    /// color) to the trailing edges (dimmed) so the arrows read as 3D.</summary>
    private void DrawTinyChevrons(int vw, int vh, int x, int y, int w, int h, Vector4 col,
                                  bool pointRight)
    {
        if (_barRenderer is null) return;
        int cw = 5, gap = 6;
        int totalW = cw * 3 + gap * 2;
        int x0 = x + (w - totalW) / 2;
        int cy = y + h / 2;
        // Tip column = full color; middle column = 78%; tail column = 55%.
        Vector4 Bright(float k) => new(col.X * k, col.Y * k, col.Z * k, col.W);
        var tip   = Bright(1.00f);
        var mid   = Bright(0.78f);
        var tail  = Bright(0.55f);
        for (int i = 0; i < 3; i++)
        {
            int cx = x0 + i * (cw + gap);
            if (pointRight)
            {
                _barRenderer.DrawRect(vw, vh, cx + 0, cy - 2, 1, 1, tail);
                _barRenderer.DrawRect(vw, vh, cx + 1, cy - 1, 1, 1, mid);
                _barRenderer.DrawRect(vw, vh, cx + 2, cy + 0, 1, 1, tip);
                _barRenderer.DrawRect(vw, vh, cx + 1, cy + 1, 1, 1, mid);
                _barRenderer.DrawRect(vw, vh, cx + 0, cy + 2, 1, 1, tail);
            }
            else
            {
                _barRenderer.DrawRect(vw, vh, cx + 2, cy - 2, 1, 1, tail);
                _barRenderer.DrawRect(vw, vh, cx + 1, cy - 1, 1, 1, mid);
                _barRenderer.DrawRect(vw, vh, cx + 0, cy + 0, 1, 1, tip);
                _barRenderer.DrawRect(vw, vh, cx + 1, cy + 1, 1, 1, mid);
                _barRenderer.DrawRect(vw, vh, cx + 2, cy + 2, 1, 1, tail);
            }
        }
    }
}
