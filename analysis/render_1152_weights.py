"""Render Figure 2 with a quantitative axis from retained component-weight CSV.
Requires Pillow and ReportLab. No model or data values are changed.
"""
from pathlib import Path
import csv
from PIL import Image,ImageDraw,ImageFont
from reportlab.pdfgen.canvas import Canvas
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
W=Path(__file__).resolve().parents[1];root=W/'working/1152' if (W/'working/1152').exists() else W
out=root/'04_FIGURE_SOURCES';stem='Figure_06_Consequence_Weights'
rows=list(csv.DictReader((out/(stem+'_source.csv')).open(encoding='utf-8-sig')))
colors=['#2A7F8E','#D99A2B','#5B9A6F','#B85450','#4F81BD'];ink='#17212B';navy='#17365D'
im=Image.new('RGB',(4800,3000),'white');d=ImageDraw.Draw(im)
def ft(size,bold=False):return ImageFont.truetype('C:/Windows/Fonts/arialbd.ttf' if bold else 'C:/Windows/Fonts/arial.ttf',size)
pdfmetrics.registerFont(TTFont('ArialAudit','C:/Windows/Fonts/arial.ttf'));pdfmetrics.registerFont(TTFont('ArialAuditBold','C:/Windows/Fonts/arialbd.ttf'))
pdf=Canvas(str(out/(stem+'.pdf')),pagesize=(480,300));svg=['<svg xmlns="http://www.w3.org/2000/svg" width="4800" height="3000" viewBox="0 0 4800 3000"><rect width="4800" height="3000" fill="white"/>']
def text(x,y,s,size=65,bold=False,anchor='la'):
    d.text((x,y),s,fill=ink,font=ft(size,bold),anchor=anchor)
    pdf.setFont('ArialAuditBold' if bold else 'ArialAudit',size/10);pdf.setFillColor(ink)
    # Pillow's 'a' anchor includes ascender offset; align the vector baseline accordingly.
    baseline=(y+size*.94)/10
    if anchor.startswith('m'):pdf.drawCentredString(x/10,300-baseline,s)
    elif anchor.startswith('r'):pdf.drawRightString(x/10,300-baseline,s)
    else:pdf.drawString(x/10,300-baseline,s)
    svg.append(f'<text x="{x}" y="{y+size*.94}" text-anchor="'+('middle' if anchor.startswith('m') else 'end' if anchor.startswith('r') else 'start')+f'" font-family="Arial" font-size="{size}" font-weight="'+('700' if bold else '400')+f'" fill="{ink}">{s}</text>')
def line(x1,y1,x2,y2,color='#DDE3E8',width=5):
    d.line((x1,y1,x2,y2),fill=color,width=width);pdf.setStrokeColor(color);pdf.setLineWidth(width/10);pdf.line(x1/10,300-y1/10,x2/10,300-y2/10);svg.append(f'<line x1="{x1}" y1="{y1}" x2="{x2}" y2="{y2}" stroke="{color}" stroke-width="{width}"/>')
text(240,105,'Frozen consequence-component weights',112,True)
text(240,270,'Dimensionless methodological-stress profile; weights sum to 1.00.',62)
left,right,top,bottom=650,4530,600,2440
for k in range(6):
    v=k/10;y=bottom-v/.5*(bottom-top);line(left,y,right,y);text(left-60,y-35,f'{v:.1f}',64,anchor='ra')
line(left,top,left,bottom,ink,5);line(left,bottom,right,bottom,ink,5)
text(210,470,'Weight',68,True)
labels=[['Failure'],['Planned','replacement'],['Intensified','monitoring'],['Targeted','inspection'],['Downtime']]
for i,(r,color,ls) in enumerate(zip(rows,colors,labels)):
    v=float(r['weight']);x=left+450+i*730;y=bottom-v/.5*(bottom-top);bw=340
    d.rectangle((x-bw/2,y,x+bw/2,bottom),fill=color);pdf.setFillColor(color);pdf.rect((x-bw/2)/10,300-bottom/10,bw/10,(bottom-y)/10,fill=1,stroke=0);svg.append(f'<rect x="{x-bw/2}" y="{y}" width="{bw}" height="{bottom-y}" fill="{color}"/>')
    text(x,y-130,f'{v:.2f}',78,True,'ma')
    for j,s in enumerate(ls):text(x,bottom+80+j*82,s,66,True,'ma')
pdf.showPage();pdf.save();svg.append('</svg>');(out/(stem+'.svg')).write_text('\n'.join(svg),encoding='utf-8')
im.save(out/(stem+'.png'),dpi=(600,600),optimize=True);im.save(out/(stem+'.tif'),dpi=(600,600),compression='tiff_lzw')
if (root/'01_UPLOAD_FILES').exists():
    (root/'01_UPLOAD_FILES'/(stem+'.tif')).write_bytes((out/(stem+'.tif')).read_bytes())
    (root/'01_UPLOAD_FILES'/(stem+'.svg')).write_bytes((out/(stem+'.svg')).read_bytes())
from docx import Document
f=next((root/'01_UPLOAD_FILES').glob('*MANUSCRIPT*.docx'),None)
if f is not None:
    doc=Document(f);shape=doc.inline_shapes[1];rid=shape._inline.graphic.graphicData.pic.blipFill.blip.embed;doc.part.related_parts[rid]._blob=(out/(stem+'.png')).read_bytes();doc.save(f)
print('1152 Figure 2: exact retained weights, numeric axis, values, 600 dpi and embedded-font vector PDF.')
