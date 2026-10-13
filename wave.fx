// The ripple that runs across the launcher when the bypass or the proxy comes on (see Wave.cs).
//
// wave.ps, which is what the launcher loads, is this file compiled for ps_2_0 with entry point "main":
//     fxc /T ps_2_0 /E main /Fo wave.ps wave.fx
// (or D3DCompile from d3dcompiler_47.dll, which is how it was built). Recompile after changing anything here.

sampler2D picture : register(s0);
float2 centre : register(c0);   // where the ripple starts, 0..1 across the picture
float radius : register(c1);    // how far its front has got, in picture heights
float amount : register(c2);    // the largest shift, in picture heights
float width : register(c3);     // half the breadth of the front, in picture heights
float aspect : register(c4);    // picture width over height

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 away = uv - centre;
    away.x *= aspect;                        // into picture heights, so the ring is round
    float far = length(away);
    float across = (far - radius) / width;   // where this pixel lies across the front; 0 is its crest
    float crest = exp(-across * across);     // dies out to either side of the front

    // a push outwards with a pull back behind it
    float shift = amount * sin(across * 1.6) * crest;
    float2 along = away / max(far, 0.0001);
    along.x /= aspect;

    float4 colour = tex2D(picture, clamp(uv - along * shift, 0.001, 0.999));
    colour.rgb += colour.rgb * crest * amount * 5;   // the crest catches a little light
    return colour;
}
