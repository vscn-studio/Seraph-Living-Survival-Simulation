"""Compile and exercise the production shader on the installed OpenGL driver in a hidden GLFW window.
Run: python tests/test_dark_adaptation_gpu.py E:/vintagestory/Vintagestory/Lib
Pixel readback is confined to this test, never the mod's renderer.
"""
import ctypes as c
from pathlib import Path
import re
import sys
import os

lib = Path(sys.argv[1]).resolve()
dll_dir = os.add_dll_directory(str(lib))
glfw = c.CDLL(str(lib / 'glfw3.dll'))
glfw.glfwCreateWindow.argtypes = [c.c_int, c.c_int, c.c_char_p, c.c_void_p, c.c_void_p]
glfw.glfwCreateWindow.restype = c.c_void_p
glfw.glfwMakeContextCurrent.argtypes = [c.c_void_p]
glfw.glfwDestroyWindow.argtypes = [c.c_void_p]
glfw.glfwGetProcAddress.argtypes = [c.c_char_p]
glfw.glfwGetProcAddress.restype = c.c_void_p
assert glfw.glfwInit(), 'GLFW init failed'
glfw.glfwWindowHint(0x00020004, 0)  # GLFW_VISIBLE=false
glfw.glfwWindowHint(0x00022002, 3)
glfw.glfwWindowHint(0x00022003, 3)
glfw.glfwWindowHint(0x00022008, 0x00032001)  # core profile
window = glfw.glfwCreateWindow(64, 64, b'SLSS shader regression', None, None)
assert window, 'Hidden OpenGL 3.3 context unavailable'
glfw.glfwMakeContextCurrent(window)

def proc(name, result, *args):
    address = glfw.glfwGetProcAddress(name.encode())
    assert address, name
    return c.WINFUNCTYPE(result, *args)(address)

uint, integer, pointer, flt = c.c_uint, c.c_int, c.c_void_p, c.c_float
create_shader = proc('glCreateShader', uint, uint)
shader_source = proc('glShaderSource', None, uint, integer, c.POINTER(c.c_char_p), c.POINTER(integer))
compile_shader = proc('glCompileShader', None, uint)
get_shader = proc('glGetShaderiv', None, uint, uint, c.POINTER(integer))
shader_log = proc('glGetShaderInfoLog', None, uint, integer, c.POINTER(integer), pointer)
create_program = proc('glCreateProgram', uint)
attach = proc('glAttachShader', None, uint, uint)
link = proc('glLinkProgram', None, uint)
get_program = proc('glGetProgramiv', None, uint, uint, c.POINTER(integer))
use = proc('glUseProgram', None, uint)
uniform = proc('glGetUniformLocation', integer, uint, c.c_char_p)
uniform1i = proc('glUniform1i', None, integer, integer)
uniform1f = proc('glUniform1f', None, integer, flt)
uniform4f = proc('glUniform4f', None, integer, flt, flt, flt, flt)
gen_texture = proc('glGenTextures', None, integer, c.POINTER(uint))
bind_texture = proc('glBindTexture', None, uint, uint)
image = proc('glTexImage2D', None, uint, integer, integer, integer, integer, integer, uint, uint, pointer)
parameter = proc('glTexParameteri', None, uint, uint, integer)
active_texture = proc('glActiveTexture', None, uint)
gen_vao = proc('glGenVertexArrays', None, integer, c.POINTER(uint))
bind_vao = proc('glBindVertexArray', None, uint)
gen_buffer = proc('glGenBuffers', None, integer, c.POINTER(uint))
bind_buffer = proc('glBindBuffer', None, uint, uint)
buffer_data = proc('glBufferData', None, uint, c.c_ssize_t, pointer, uint)
attrib = proc('glVertexAttribPointer', None, uint, integer, uint, c.c_ubyte, integer, pointer)
enable_attrib = proc('glEnableVertexAttribArray', None, uint)
draw = proc('glDrawArrays', None, uint, integer, integer)
viewport = proc('glViewport', None, integer, integer, integer, integer)
read = proc('glReadPixels', None, integer, integer, integer, integer, uint, uint, pointer)
get_string = proc('glGetString', c.c_char_p, uint)
get_error = proc('glGetError', uint)

try:
    source = (Path(__file__).resolve().parents[1] / 'src/Seraph/Client/DarkAdaptationRenderer.cs').read_text(encoding='utf-8')
    program = create_program()
    for name, kind in [('Vertex', 0x8B31), ('Fragment', 0x8B30)]:
        code = re.search(r'private const string ' + name + r' = """(.*?)""";', source, re.S)[1].encode()
        shader = create_shader(kind)
        shader_source(shader, 1, c.byref(c.c_char_p(code)), None)
        compile_shader(shader)
        status = integer()
        get_shader(shader, 0x8B81, c.byref(status))
        if not status.value:
            log = c.create_string_buffer(8192)
            shader_log(shader, len(log), None, log)
            raise AssertionError(log.value.decode())
        attach(program, shader)
    link(program)
    status = integer()
    get_program(program, 0x8B82, c.byref(status))
    assert status.value, 'Shader program link failed'
    use(program)
    vao, vbo = uint(), uint()
    gen_vao(1, c.byref(vao)); bind_vao(vao)
    gen_buffer(1, c.byref(vbo)); bind_buffer(0x8892, vbo)
    vertices = (flt * 30)(-1,-1,0,0,0, 1,-1,0,1,0, 1,1,0,1,1, -1,-1,0,0,0, 1,1,0,1,1, -1,1,0,0,1)
    buffer_data(0x8892, c.sizeof(vertices), vertices, 0x88E4)
    attrib(0, 3, 0x1406, 0, 20, None); enable_attrib(0)
    attrib(1, 2, 0x1406, 0, 20, c.c_void_p(12)); enable_attrib(1)
    textures = (uint * 2)()
    gen_texture(2, textures)
    for i in range(2):
        active_texture(0x84C0 + i); bind_texture(0x0DE1, textures[i])
        parameter(0x0DE1, 0x2801, 0x2600); parameter(0x0DE1, 0x2800, 0x2600)
        parameter(0x0DE1, 0x2802, 0x812F); parameter(0x0DE1, 0x2803, 0x812F)
    uniform1i(uniform(program, b'sceneTexture'), 0); uniform1i(uniform(program, b'depthTexture'), 1)
    p10, p14 = -(100 + .1)/(100 - .1), -2*100*.1/(100 - .1)
    uniform4f(uniform(program, b'projection'), 1, 1, p10, p14)
    viewport(0, 0, 64, 64)
    def render(distance, strength=1, color=(0,0,0), sky=False, xy=(32,32), frame=0, full=False):
        colors = [v for y in range(64) for x in range(64) for v in color(x,y)] if callable(color) else color * (64*64)
        rgb = (flt * (64*64*3))(*colors)
        depth = (1 if sky else (p14 / distance - p10 + 1) / 2)
        depth_data = (flt * (64*64))(*([depth] * (64*64)))
        active_texture(0x84C0); bind_texture(0x0DE1, textures[0])
        image(0x0DE1, 0, 0x8815, 64, 64, 0, 0x1907, 0x1406, rgb)
        active_texture(0x84C1); bind_texture(0x0DE1, textures[1])
        image(0x0DE1, 0, 0x822E, 64, 64, 0, 0x1903, 0x1406, depth_data)
        uniform1f(uniform(program, b'strength'), strength)
        uniform1f(uniform(program, b'noiseFrame'), frame)
        draw(0x0004, 0, 6)
        pixel = (c.c_ubyte * (64*64*4 if full else 4))()
        read(0 if full else xy[0], 0 if full else xy[1], 64 if full else 1, 64 if full else 1, 0x1908, 0x1401, pixel)
        assert get_error() == 0, 'OpenGL error'
        return bytes(pixel) if full else tuple(pixel)[:3]
    near = render(2)
    fading = render(5)
    far = render(8)
    early = render(2, .075)
    assert 2 < near[0] <= 31 and near[2] >= near[0], near
    assert 0 < fading[0] < near[0] and far == (0,0,0), (near, fading, far)
    assert early[0] < near[0] and render(2, 0) == (0,0,0), early
    assert render(2, sky=True) == (0,0,0), 'Sky brightened'
    tinted = render(2, color=(.03,.01,.005))
    assert max(tinted) - min(tinted) <= 3, ('Low light retained color', tinted)
    original = render(2, 0, (.8,.2,.1))
    assert render(2, 1, (.8,.2,.1)) == original, 'Bright source altered'
    assert render(8, 1, (.02,.01,.005)) == render(8, 0, (.02,.01,.005)), 'Distant color changed'
    levels = [render(2, color=(v,v,v))[1] for v in (.002,.008,.02,.04)]
    assert all(a < b for a,b in zip(levels, levels[1:])), ('Shadow detail collapsed to flat gray', levels)
    center = render(2, color=(.015,)*3)
    edge = render(2, color=(.015,)*3, xy=(57,32))
    assert edge[1] > center[1] and edge[2] > edge[0], ('Peripheral sensitivity/cool tint missing', center, edge)
    stripes = lambda x,y: (.012 if x % 2 else .02,)*3
    contrast_before = render(2,0,stripes,xy=(32,32))[1] - render(2,0,stripes,xy=(33,32))[1]
    contrast_after = render(2,1,stripes,xy=(32,32))[1] - render(2,1,stripes,xy=(33,32))[1]
    assert contrast_after > contrast_before, ('Central fine detail blurred', contrast_before, contrast_after)
    grain0 = render(2, full=True); grain1 = render(2, frame=1, full=True)
    deltas = [abs(a-b) for i,(a,b) in enumerate(zip(grain0,grain1)) if i % 4 != 3]
    assert 0 < max(deltas) <= 2, ('Grain absent or excessive', max(deltas))
    assert render(2, color=(.05,)*3, frame=0) == render(2, color=(.05,)*3, frame=1), 'Grain outside extreme darkness'
    print('PASS production GLSL: selective shadow detail, cool desaturation, sharp centre, peripheral sensitivity, subtle dark-only grain, 4-6 m fade, unchanged sky/distant/highlights; ' + get_string(0x1F02).decode())
finally:
    glfw.glfwDestroyWindow(window)
    glfw.glfwTerminate()
