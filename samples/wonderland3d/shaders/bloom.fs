#version 330
in vec2 fragTexCoord;
uniform sampler2D texture0;
out vec4 finalColor;
void main() {
    vec2 uv = fragTexCoord;
    vec2 px = 1.0 / vec2(textureSize(texture0, 0));
    vec3 base = texture(texture0, uv).rgb;
    vec3 glow = vec3(0.0);
    for (int y = -3; y <= 3; y++) {
        for (int x = -3; x <= 3; x++) {
            vec3 s = texture(texture0, uv + vec2(x,y) * px * 3.5).rgb;
            glow += max(s - 0.45, 0.0) / 49.0;
        }
    }
    vec3 c = base + glow * 1.35;
    float vignette = 1.0 - 0.5 * dot((uv - 0.5) * 1.4, (uv - 0.5) * 1.4);
    finalColor = vec4(pow(c * vignette, vec3(0.94)), 1.0);
}
