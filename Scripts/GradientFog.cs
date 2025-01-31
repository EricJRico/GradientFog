using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

public class GradientFog : ScriptableRendererFeature
{
    [System.Serializable]
    public class GradientFogSettings
    {
        // Needed requirements for the pass
        public ScriptableRenderPassInput requirements =
            ScriptableRenderPassInput.Color | ScriptableRenderPassInput.Depth;

        public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;
        public float startDistance;
        public float endDistance = 100;
        public Color nearColor = new(0, 0.2f, 0.35f, 1);
        public Color middleColor = new(0.62f, 0.86f, 1, 1);
        public Color farColor = new(0.85f, 0.96f, 1, 1);
    }

    GradientFogPass _gradientFogPass;
    [SerializeField] GradientFogSettings settings = new();
    private static MaterialPropertyBlock _sharedPropertyBlock = null;

    public override void Create()
    {
        _gradientFogPass = new GradientFogPass(settings);
    }

    public void OnEnable()
    {
        if (_sharedPropertyBlock == null)
            _sharedPropertyBlock = new MaterialPropertyBlock();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_gradientFogPass);
    }

    public class GradientFogPass : ScriptableRenderPass
    {
        private readonly GradientFogSettings _settings;
        private readonly Material FogMaterial = new(Shader.Find("Shader Graphs/GradientFog"));
        private static readonly Material FrameBufferFetchMaterial = new(Shader.Find("Custom/FrameBufferFetch"));
        private static readonly int StartDist = Shader.PropertyToID("_StartDist");
        private static readonly int EndDist = Shader.PropertyToID("_EndDist");
        private static readonly int NearColor = Shader.PropertyToID("_NearColor");
        private static readonly int MidColor = Shader.PropertyToID("_MidColor");
        private static readonly int FarColor = Shader.PropertyToID("_FarColor");
        private static readonly int BlitTextureID = Shader.PropertyToID("_BlitTexture");
        private static readonly int BlitScaleBiasID = Shader.PropertyToID("_BlitScaleBias");
        private readonly string _passName;
        private ProfilingSampler _sampler;


        public GradientFogPass(GradientFogSettings settings)
        {
            ConfigureInput(settings.requirements);
            _passName = "Gradient Fog";
            renderPassEvent = settings.renderPassEvent;
            _settings = settings;
        }

        // RecordRenderGraph is where the RenderGraph handle can be accessed, through which render passes can be added to the graph.
        // FrameData is a context container through which URP resources can be accessed and managed.
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (FogMaterial == null)
                return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            // We need a copy of the color texture as input for the blit with material
            // Retrieving texture descriptor from active color texture after post process
            var colCopyDesc = renderGraph.GetTextureDesc(resourceData.afterPostProcessColor);
            // Changing the name
            colCopyDesc.name = "_TempColorCopy";
            // Requesting the creation of a texture to Render Graph, Render Graph will allocate when needed
            TextureHandle copiedColorTexture = renderGraph.CreateTexture(colCopyDesc);

            // Set all the properties on the material
            FogMaterial.SetFloat(StartDist, _settings.startDistance);
            FogMaterial.SetFloat(EndDist, _settings.endDistance);
            FogMaterial.SetColor(NearColor, _settings.nearColor);
            FogMaterial.SetColor(MidColor, _settings.middleColor);
            FogMaterial.SetColor(FarColor, _settings.farColor);

            // First blit, simply copying color to intermediary texture so it can be used as input in next pass
            using (var builder =
                   renderGraph.AddRasterRenderPass<PassData>(_passName + "_CopyPass", out var passData, _sampler))
            {
                // Setting the URP active color texture as the source for this pass
                passData.Source = resourceData.activeColorTexture;

                // Setting input texture to sample
                builder.SetInputAttachment(resourceData.activeColorTexture, 0);
                // Setting output attachment
                builder.SetRenderAttachment(copiedColorTexture, 0, AccessFlags.Write);

                // Execute step, simple copy
                builder.SetRenderFunc((PassData data, RasterGraphContext rgContext) =>
                {
                    ExecuteCopyColorPass(rgContext.cmd);
                });
            }

            // Second blit with material, applying gray conversion
            using (var builder =
                   renderGraph.AddRasterRenderPass<PassData>(_passName + "_FullScreenPass", out var passData,
                       _sampler))
            {
                // Setting the temp color texture as the source for this pass
                passData.Source = resourceData.activeColorTexture;
                // Setting the material
                passData.Material = FogMaterial;

                // Setting input texture to sample
                builder.SetInputAttachment(copiedColorTexture, 0);
                // Setting output attachment
                builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);

                // Execute step, second blit with the gray scale conversion
                builder.SetRenderFunc((PassData data, RasterGraphContext rgContext) =>
                {
                    ExecuteMainPass(rgContext.cmd, data.Material, data.Source);
                });
            }
        }

        private class PassData
        {
            internal Material Material;
            internal TextureHandle Source;
        }

        private static void ExecuteCopyColorPass(RasterCommandBuffer cmd)
        {
            cmd.DrawProcedural(Matrix4x4.identity, FrameBufferFetchMaterial, 1, MeshTopology.Triangles, 3, 1, null);
        }

        private static void ExecuteMainPass(RasterCommandBuffer cmd, Material material, RTHandle copiedColor)
        {
            _sharedPropertyBlock.Clear();
            if (copiedColor != null)
                _sharedPropertyBlock.SetTexture(BlitTextureID, copiedColor);

            // We need to set the "_BlitScaleBias" uniform for user materials with shaders relying on core Blit.hlsl to work
            _sharedPropertyBlock.SetVector(BlitScaleBiasID, new Vector4(1, 1, 0, 0));

            cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Triangles, 3, 1, _sharedPropertyBlock);
        }
    }
}