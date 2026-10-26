"""Reflow one overflowing SVG label; retained scientific content is unchanged."""
from pathlib import Path
import argparse,xml.etree.ElementTree as ET,math,json,hashlib,shutil
from PIL import Image,ImageDraw,ImageFont
ap=argparse.ArgumentParser(description=__doc__);ap.add_argument('--source',type=Path,required=True);ap.add_argument('--output-dir',type=Path,required=True);a=ap.parse_args();a.output_dir.mkdir(parents=True,exist_ok=True)
s=a.source.read_text(encoding='utf-8')
old='<text x="1485.0" y="713.0" text-anchor="middle" font-family="Arial" font-size="35" font-weight="700" fill="#17212B">Limited claims</text>\n<text x="1485.0" y="757.0" text-anchor="middle" font-family="Arial" font-size="35" font-weight="700" fill="#17212B">C1 partial / C2 yes / C3 partial</text>'
new='<text x="1485.0" y="690.0" text-anchor="middle" font-family="Arial" font-size="35" font-weight="700" fill="#17212B">Limited claims</text>\n<text x="1485.0" y="735.0" text-anchor="middle" font-family="Arial" font-size="35" font-weight="700" fill="#17212B">C1 partial / C2 yes /</text>\n<text x="1485.0" y="780.0" text-anchor="middle" font-family="Arial" font-size="35" font-weight="700" fill="#17212B">C3 partial</text>'
assert s.count(old)==1,'Unexpected source label; do not alter a different image'
fixed=s.replace(old,new);svg=a.output_dir/a.source.name;svg.write_text(fixed,encoding='utf-8')
root=ET.fromstring(fixed);scale=6050/2400;im=Image.new('RGB',(6050,round(1350*scale)),'white');draw=ImageDraw.Draw(im)
def val(el,key,default=0):
    v=el.get(key,str(default));return float(v.replace('%','')) if '%' not in v else (2400 if key=='width' else 1350)
for e in list(root):
    tag=e.tag.split('}')[-1]
    if tag=='defs':continue
    if tag=='rect':
        x=val(e,'x');y=val(e,'y');w=val(e,'width');h=val(e,'height');r=val(e,'rx');coords=[x*scale,y*scale,(x+w)*scale,(y+h)*scale];fill=e.get('fill','white');stroke=e.get('stroke');width=max(1,round(val(e,'stroke-width',1)*scale))
        if r:draw.rounded_rectangle(coords,r*scale,fill=fill,outline=stroke,width=width)
        else:draw.rectangle(coords,fill=fill,outline=stroke,width=width)
    elif tag=='text':
        x=val(e,'x')*scale;y=val(e,'y')*scale;size=val(e,'font-size')*scale;bold=e.get('font-weight')=='700';font=ImageFont.truetype('C:/Windows/Fonts/arialbd.ttf' if bold else 'C:/Windows/Fonts/arial.ttf',round(size));anchor='ms' if e.get('text-anchor')=='middle' else 'ls'
        draw.text((x,y),e.text or '',font=font,fill=e.get('fill','#17212B'),anchor=anchor)
        if 'C1 partial' in (e.text or '') or 'C3 partial'==(e.text or ''):
            bbox=draw.textbbox((x,y),e.text,font=font,anchor=anchor);assert bbox[0]>(1250+8)*scale and bbox[2]<(1720-8)*scale,'Reflowed label exceeds its box'
    elif tag=='line':
        x1,y1,x2,y2=[val(e,k)*scale for k in ['x1','y1','x2','y2']];color=e.get('stroke','#66727C');draw.line((x1,y1,x2,y2),fill=color,width=round(val(e,'stroke-width',1)*scale))
        if e.get('marker-end'):
            angle=math.atan2(y2-y1,x2-x1);length=72*scale;width=35*scale;bx=x2-length*math.cos(angle);by=y2-length*math.sin(angle);draw.polygon([(x2,y2),(bx+width*math.sin(angle),by-width*math.cos(angle)),(bx-width*math.sin(angle),by+width*math.cos(angle))],fill=color)
png=svg.with_suffix('.png');tif=svg.with_suffix('.tif');im.save(png,dpi=(1000,1000));im.save(tif,dpi=(1000,1000),compression='tiff_lzw')
audit=dict(scope='Wrap overflowing final claim label; preserve all scientific words and diagram connections',source_sha256=hashlib.sha256(a.source.read_bytes()).hexdigest(),svg_sha256=hashlib.sha256(svg.read_bytes()).hexdigest(),png_sha256=hashlib.sha256(png.read_bytes()).hexdigest(),tiff_sha256=hashlib.sha256(tif.read_bytes()).hexdigest(),pixels=im.size,dpi=1000,label_bounds='pass',raster_engine='Pillow, system Arial/Arial Bold; fonts not redistributed')
(a.output_dir/'FIGURE_03_LAYOUT_CORRECTION.json').write_text(json.dumps(audit,indent=2),encoding='utf-8');print(json.dumps(audit))
