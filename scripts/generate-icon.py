"""Generate the application's geometric green orb icon; standard library only."""
from pathlib import Path
import math, struct, zlib

def chunk(kind, data):
    return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data))

def png(size):
    rows = bytearray()
    for y in range(size):
        rows.append(0)
        for x in range(size):
            rgb = [0., 0., 0.]; alpha = 0.
            for sy in range(4):
                for sx in range(4):
                    px = ((x + (sx+.5)/4) / size - .5) / .43
                    py = ((y + (sy+.5)/4) / size - .5) / .43
                    r2 = px*px + py*py
                    if r2 > 1: continue
                    z = math.sqrt(1-r2)
                    light = max(0, -.38*px-.5*py+.78*z)
                    shine = math.exp(-((px+.32)**2+(py+.38)**2)/.045)
                    colors = (15+53*light+103*shine, 94+111*light+39*shine, 49+66*light+96*shine)
                    for i in range(3): rgb[i] += min(255, colors[i])
                    alpha += 1
            rows.extend([*(round(c/alpha) if alpha else 0 for c in rgb), round(255*alpha/16)])
    return b'\x89PNG\r\n\x1a\n'+chunk(b'IHDR', struct.pack('>IIBBBBB',size,size,8,6,0,0,0))+chunk(b'IDAT',zlib.compress(rows,9))+chunk(b'IEND',b'')

sizes = [16, 20, 24, 32, 48, 64, 128, 256]
frames = [png(s) for s in sizes]
header = bytearray(struct.pack('<HHH',0,1,len(sizes)))
offset = 6+16*len(sizes)
for size, frame in zip(sizes,frames):
    header.extend(struct.pack('<BBBBHHII',size%256,size%256,0,0,1,32,len(frame),offset)); offset += len(frame)
assets = Path(__file__).resolve().parents[1]/'src/CodexNotifier.Desktop/Assets'
(assets/'app.ico').write_bytes(header+b''.join(frames))
(assets/'app-preview.png').write_bytes(frames[-1])
