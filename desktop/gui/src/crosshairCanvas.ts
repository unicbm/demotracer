/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/
import { rasterizeCrosshairFrame, type CrosshairPrimitive } from "./crosshairRenderer.ts";

const VERTEX = `#version 300 es
void main() {
  vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
  gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
}`;
// Four vec4s per primitive, at most nine primitives for preview styles 0..9.
// The CPU reference uses the same inclusive pixel grid and max-alpha layering.
const FRAGMENT = `#version 300 es
precision highp float;
uniform vec4 shapes[36];
uniform int count;
uniform vec2 viewport;
out vec4 result;
const float PI = 3.141592653589793;
float ramp(float a, float b, float x) {
  float t = clamp((x-a)/(b-a), 0.0, 1.0);
  return t*t*(3.0-2.0*t);
}
vec3 srgb(vec3 x) {
  return mix(1.055*pow(max(x,vec3(0.0)),vec3(1.0/2.4))-0.055, x*12.92, lessThanEqual(x,vec3(0.0031308)));
}
void main() {
  vec2 pixel = floor(vec2(gl_FragCoord.x, viewport.y-gl_FragCoord.y));
  vec4 fill = vec4(0.0), border = vec4(0.0);
  for (int i=0; i<9; i++) {
    if (i>=count) break;
    vec4 g=shapes[i*4], p=shapes[i*4+1];
    float fc=0.0, bc=0.0;
    if (p.x==0.0) {
      fc = all(greaterThanEqual(pixel,g.xy)) && all(lessThanEqual(pixel,g.zw)) ? 1.0 : 0.0;
      bc = all(greaterThanEqual(pixel,g.xy-p.z)) && all(lessThanEqual(pixel,g.zw+p.w)) ? 1.0 : 0.0;
    } else {
      vec2 delta=pixel-g.xy;
      float d=length(delta), a=atan(delta.x,-delta.y);
      float blend=max(ramp(0.0,PI/2.0,a),ramp(-PI/2.0,-PI,a));
      float extent=mix(p.z,p.w,blend), outer=g.z+extent, inner=g.z-g.w-mix(p.w,p.z,blend);
      fc=ramp(g.z+0.5,g.z-0.5,d)*ramp(g.z-g.w-0.5,g.z-g.w+0.5,d);
      bc=ramp(outer+0.5,outer-0.5,d)*ramp(inner-0.5,inner+0.5,d);
      if (p.x==2.0) {
        float q=abs(a-floor(a/(PI/2.0))*PI/2.0-PI/4.0), r=1.0/max(d,1.0), arc=p.y/2.0;
        fc*=ramp(arc+0.5*r,arc-0.5*r,q);
        bc*=ramp(arc+(extent+0.5)*r,arc+(extent-0.5)*r,q);
      }
    }
    vec4 f=shapes[i*4+2], b=shapes[i*4+3];
    f.a*=fc; b.a*=bc;
    if (f.a>=fill.a) fill=f;
    if (b.a>=border.a) border=b;
  }
  float b=border.a*(1.0-fill.a)*(1.0-fill.a), a=fill.a+b;
  vec3 rgb=a>0.0 ? srgb((fill.rgb*fill.a+border.rgb*b)/a) : vec3(0.0);
  result=vec4(rgb*a,a);
}`;

export interface CrosshairCanvas {
  draw(shapes: readonly CrosshairPrimitive[], width: number, height: number): void;
  dispose(): void;
}

// Returning null lets the component mount a fresh 2D canvas. Browsers cannot
// switch an existing canvas from a failed WebGL context to a 2D context.
export function createCrosshairCanvas(canvas: HTMLCanvasElement, cpu = false): CrosshairCanvas | null {
  if (cpu) {
    const context = canvas.getContext("2d");
    if (!context) return null;
    return {
      draw(shapes, width, height) {
        if (canvas.width !== width) canvas.width = width;
        if (canvas.height !== height) canvas.height = height;
        const pixels = rasterizeCrosshairFrame(shapes, width, height);
        const image = context.createImageData(width, height);
        image.data.set(pixels.data);
        context.putImageData(image, 0, 0);
      },
      dispose() { context.clearRect(0, 0, canvas.width, canvas.height); },
    };
  }
  const gl = canvas.getContext("webgl2", { alpha: true, premultipliedAlpha: true, antialias: false, depth: false, stencil: false });
  if (!gl) return null;
  const program = gl.createProgram();
  if (!program) return null;
  const shaders: WebGLShader[] = [];
  const dispose = () => { for (const shader of shaders) gl.deleteShader(shader); gl.deleteProgram(program); };
  for (const [type, source] of [[gl.VERTEX_SHADER, VERTEX], [gl.FRAGMENT_SHADER, FRAGMENT]] as const) {
    const shader = gl.createShader(type);
    if (!shader) { dispose(); return null; }
    shaders.push(shader);
    gl.shaderSource(shader, source); gl.compileShader(shader);
    if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) { dispose(); return null; }
    gl.attachShader(program, shader);
  }
  gl.linkProgram(program);
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) { dispose(); return null; }
  const packed = new Float32Array(36 * 4);
  const shapeLocation = gl.getUniformLocation(program, "shapes[0]");
  const countLocation = gl.getUniformLocation(program, "count");
  const viewportLocation = gl.getUniformLocation(program, "viewport");
  return {
    draw(shapes, width, height) {
      if (canvas.width !== width) canvas.width = width;
      if (canvas.height !== height) canvas.height = height;
      shapes.slice(0, 9).forEach((shape, index) => {
        const offset = index * 16;
        packed.set(shape.geometry, offset);
        packed.set([shape.kind, shape.arc, ...shape.outline], offset + 4);
        packed.set(shape.fill, offset + 8); packed.set(shape.border, offset + 12);
      });
      gl.viewport(0, 0, width, height); gl.useProgram(program);
      gl.uniform4fv(shapeLocation, packed); gl.uniform1i(countLocation, Math.min(shapes.length, 9));
      gl.uniform2f(viewportLocation, width, height);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
    },
    dispose,
  };
}
