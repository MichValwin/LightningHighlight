using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

class HighlightContext(BlockPos pos, MeshRef meshRef) {
    public BlockPos Pos => pos;
    public MeshRef MeshRef = meshRef;
}

class HightlightRenderer : IRenderer {
    private ICoreClientAPI _api;

    public double RenderOrder => 0.89;
    public int RenderRange => 256;
    public HighlightContext? Context;

    public HightlightRenderer(ICoreClientAPI api) {
        _api = api;
    }

    public void Dispose() {
        if (Context != null) {
            _api.Render.DeleteMesh(Context.MeshRef);
        }
    }

    public void OnRenderFrame(float deltaTime, EnumRenderStage stage) {
        if (Context == null) {
            return;
        }

        var highlighter = ShaderPrograms.Blockhighlights;
        highlighter.Use();

        _api.Render.GlPushMatrix();
        _api.Render.GlLoadMatrix(_api.Render.CameraMatrixOrigin);

        var cameraPos = _api.World.Player.Entity.CameraPos;
        _api.Render.GlTranslate(Context.Pos.X - cameraPos.X, Context.Pos.Y - cameraPos.Y, Context.Pos.Z - cameraPos.Z);

        highlighter.ModelViewMatrix = _api.Render.CurrentModelviewMatrix;
        highlighter.ProjectionMatrix = _api.Render.CurrentProjectionMatrix;

        _api.Render.RenderMesh(Context.MeshRef);
        _api.Render.GlPopMatrix();

        highlighter.Stop();
    }
}