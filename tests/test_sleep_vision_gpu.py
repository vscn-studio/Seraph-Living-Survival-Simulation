"""Exercise the production symptom shader and Gaussian passes on a hidden OpenGL context."""
import ctypes as c
from pathlib import Path
import re
import sys
import os
import tempfile

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

gen_fbo = proc('glGenFramebuffers', None, integer, c.POINTER(uint))
bind_fbo = proc('glBindFramebuffer', None, uint, uint)
attach_texture = proc('glFramebufferTexture2D', None, uint, uint, uint, uint, integer)
check_fbo = proc('glCheckFramebufferStatus', uint, uint)
uniform2f = proc('glUniform2f', None, integer, flt, flt)
try:
    source = (Path(__file__).resolve().parents[1] / 'src/Seraph/Client/SymptomPostProcess.cs').read_text(encoding='utf-8')
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
    from PIL import Image, ImageDraw
    import math
    W, H = 512, 288
    textures = (uint * 4)(); gen_texture(4, textures)
    fbos = (uint * 3)(); gen_fbo(3, fbos)
    for i in range(4):
        active_texture(0x84C0); bind_texture(0x0DE1, textures[i])
        w, h = (W//2,H//2) if i in (1,2) else (W,H)
        image(0x0DE1,0,0x8058,w,h,0,0x1908,0x1401,None)
        for k,v in [(0x2801,0x2601),(0x2800,0x2601),(0x2802,0x812F),(0x2803,0x812F)]: parameter(0x0DE1,k,v)
        if i:
            bind_fbo(0x8D40,fbos[i-1]); attach_texture(0x8D40,0x8CE0,0x0DE1,textures[i],0)
            assert check_fbo(0x8D40) == 0x8CD5
    def scalar(name,value): uniform1f(uniform(program,name.encode()),value)
    def flag(name,value): uniform1i(uniform(program,name.encode()),value)
    def texture(name,index,unit):
        active_texture(0x84C0+unit); bind_texture(0x0DE1,textures[index]); flag(name,unit)
    uniform4f(uniform(program,b'screenRect'),-1,-1,2,2)
    uniform4f(uniform(program,b'edgeTint'),0,0,0,0)
    uniform2f(uniform(program,b'pixelSize'),1/W,1/H)
    def render(source, blur=0, darkness=0, lid=0, closure=0, ghost=0, time=0):
        source=source.convert('RGBA')
        texture('sceneTexture',0,0)
        raw=c.create_string_buffer(source.tobytes())
        image(0x0DE1,0,0x8058,W,H,0,0x1908,0x1401,raw)
        flag('noticeMode',0)
        if blur:
            radius=(.35+3.3*blur)*H/1080*(1+.10*math.sin(time*.8)+.04*math.sin(time*1.7))
            flag('blurPass',1); viewport(0,0,W//2,H//2)
            for i in range(2):
                bind_fbo(0x8D40,fbos[i]); texture('sceneTexture',i,0)
                uniform2f(uniform(program,b'blurAxis'),radius/W if i==0 else 0,radius/H if i==1 else 0)
                draw(0x0004,0,6)
        bind_fbo(0x8D40,fbos[2]); viewport(0,0,W,H)
        flag('blurPass',0); flag('hasBlur',int(blur>0)); flag('overlayOnly',0)
        texture('sceneTexture',0,0); texture('blurTexture',2,1)
        for n,v in [('blurStrength',blur),('darkness',darkness),('centerDarkness',0),('upperLid',lid),('closure',closure),('ghost',ghost),('time',time),('night',0)]: scalar(n,v)
        draw(0x0004,0,6)
        pixels=(c.c_ubyte*(W*H*4))(); read(0,0,W,H,0x1908,0x1401,pixels)
        assert get_error()==0, 'OpenGL error'
        return Image.frombytes('RGBA',(W,H),bytes(pixels))
    flat=Image.new('RGB',(W,H),(128,128,128))
    assert render(flat,.5).getpixel((W//2,H//2))[:3] == (128,128,128), 'Gaussian changes brightness'
    edge=Image.new('RGB',(W,H),'black'); d=ImageDraw.Draw(edge); d.rectangle((W//2,0,W,H),fill='white')
    original=render(edge)
    blurred=render(edge,.5)
    row=[blurred.getpixel((x,H//2))[0] for x in range(W//2-10,W//2+10)]
    assert all(a<=b for a,b in zip(row,row[1:])), ('Ringing/glass-like edge',row)
    assert sum(0<v<255 for v in row)>=4, ('Object edge not defocused',row)
    mild=render(edge,.06)
    assert original.getpixel((W//2-1,H//2))[0] < mild.getpixel((W//2-1,H//2))[0] < blurred.getpixel((W//2-1,H//2))[0], 'Defocus not gradual'
    # The same edge must soften across the frame, not only at the periphery.
    assert blurred.getpixel((W//2-1,20)) == blurred.getpixel((W//2-1,H//2)), 'Blur varies spatially'
    dim=render(flat,darkness=.32)
    assert dim.getpixel((5,H//2))[0] < dim.getpixel((W//2,H//2))[0], 'Missing peripheral dimming'
    lid=render(flat,lid=.32)
    assert lid.getpixel((W//2,H-5))[0] == 0 and lid.getpixel((W//2,5))[0] == 128, 'Upper lid incorrectly covers bottom'
    assert render(flat,closure=1).getextrema()[:3] == ((0,0),)*3, 'Full closure is not black'
    focused=render(edge,.5,time=1.2)
    ghosted=render(edge,.5,ghost=.09,time=1.2)
    changes=[abs(a-b) for i,(a,b) in enumerate(zip(focused.tobytes(),ghosted.tobytes())) if i%4 !=3]
    assert 0 < max(changes) < 20, ('Ghost absent/excessive',max(changes))
    assert render(edge,.5,time=0).tobytes() != render(edge,.5,time=2).tobytes(), 'Focus never breathes'
    # Export synthetic graphics to inspect actual shader appearance, not an in-game screenshot.
    chart=Image.new('RGB',(W,H),(37,47,52)); d=ImageDraw.Draw(chart)
    for x in range(15,W-15,40):
        d.rectangle((x,45,x+18,240),fill=(160,170,150)); d.line((x,45,x+18,45),fill='white',width=2)
    d.ellipse((225,105,285,170),fill=(210,130,70)); d.text((22,15),'FOCUS / OBJECT EDGES',fill='white')
    montage=Image.new('RGB',(W*3,H))
    for i,args in enumerate([{},dict(blur=.18,darkness=.16,lid=.12),dict(blur=.5,darkness=.32,lid=.32,ghost=.09,time=1.2)]):
        result=render(chart.transpose(Image.FLIP_TOP_BOTTOM),**args).transpose(Image.FLIP_TOP_BOTTOM)
        montage.paste(result,(W*i,0))
    output=Path(tempfile.gettempdir()) / 'Seraph-tests' / 'sleep-vision-render'; output.mkdir(parents=True,exist_ok=True)
    montage.save(output/'normal-90-100.png')
    print('PASS production symptom GLSL/two-pass GPU: smooth monotonic edges, gradual full-frame defocus, no brightness shift, edge dimming, upper lid/full closure, subtle ghost and focus drift; '+get_string(0x1F02).decode())
finally:
    glfw.glfwDestroyWindow(window)
    glfw.glfwTerminate()
