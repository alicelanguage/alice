#version 330
in vec3 normal;
in vec3 position;
in vec4 color;
out vec4 finalColor;
void main() {
    vec3 face = cross(dFdx(position), dFdy(position));
    vec3 n = normalize(face);
    float key = max(dot(n, normalize(vec3(-0.4, 0.8, 0.5))), 0.0);
    float rim = pow(1.0 - abs(n.z), 3.0);
    vec3 c = color.rgb * (0.48 + 0.65 * key);
    c += vec3(0.18, 0.10, 0.28) * rim;
    float distance = gl_FragCoord.z / gl_FragCoord.w;
    float fog = 1.0 - exp(-distance * 0.018);
    finalColor = vec4(mix(c, vec3(0.018, 0.012, 0.055), fog), color.a);
}
