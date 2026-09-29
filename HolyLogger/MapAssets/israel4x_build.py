import json, math
from PIL import Image, ImageDraw, ImageChops, ImageFilter
Image.MAX_IMAGE_PIXELS=None
W0,E0,S0,N0 = 27.0, 43.0, 27.0, 35.5          # region (degrees)
SC = 3                                        # output px per SR_HR px (1/60 deg)
def my(lat): return math.log(math.tan(math.pi/4+math.radians(lat)/2))
Wpx = int((E0-W0)*60*SC)
ky = Wpx/math.radians(E0-W0)                  # mercator px per radian
Hpx = int(round((my(N0)-my(S0))*ky))
def px(lon,lat): return ((lon-W0)/(E0-W0)*Wpx, (my(N0)-my(lat))*ky)
print('image',Wpx,Hpx)

def rings(geom):
    t,c=geom['type'],geom['coordinates']
    if t=='Polygon': return [c]
    if t=='MultiPolygon': return c
    return []
def near(ring,m=1.0):
    xs=[p[0] for p in ring]; ys=[p[1] for p in ring]
    return max(xs)>W0-m and min(xs)<E0+m and max(ys)>S0-m and min(ys)<N0+m
def fill(draw,path,color):
    for f in json.load(open(path,encoding='utf-8'))['features']:
        for poly in rings(f['geometry']):
            if not near(poly[0]): continue
            draw.polygon([px(x,y) for x,y in poly[0]], fill=color)


SEA=(170,211,223); LAND=(239,235,216); URBAN=(224,218,208)
# LAND COLOUR: STYLE='north-south' (the default he chose): green in the north fading to sand in
# the south.
import os
STYLE=os.environ.get('LAND_STYLE','north-south')   # his choice, 2026-09-29
if 'LAND_RGB' in os.environ: LAND=tuple(int(os.environ['LAND_RGB'][i:i+2],16) for i in (0,2,4))
base=Image.new('RGB',(Wpx,Hpx),SEA); d=ImageDraw.Draw(base)
fill(d,'ne_10m_land.geojson',LAND)
if STYLE=='north-south':
    NORTH=(200,224,170); SAND=(242,224,178)      # green in the north, sand in the south (his choice)
    grad=Image.new('RGB',(Wpx,Hpx))
    gd=ImageDraw.Draw(grad)
    for row in range(Hpx):
        lat=math.degrees(2*math.atan(math.exp(my(N0)-row/ky))-math.pi/2)
        t=min(1,max(0,(32.6-lat)/(32.6-30.6)))      # 0 north of 32.6, 1 south of 30.6
        gd.line([(0,row),(Wpx,row)],fill=tuple(int(NORTH[k]*(1-t)+SAND[k]*t) for k in range(3)))
    lm=Image.new('L',(Wpx,Hpx),0); fill(ImageDraw.Draw(lm),'ne_10m_land.geojson',255)
    base.paste(grad,mask=lm)
fill(d,'ne_10m_urban_areas.geojson',URBAN)
landmask=Image.new('L',(Wpx,Hpx),0); dm=ImageDraw.Draw(landmask)
fill(dm,'ne_10m_land.geojson',255)
lakes=Image.new('L',(Wpx,Hpx),0); dl=ImageDraw.Draw(lakes)
fill(dl,'ne_10m_lakes.geojson',255)
landmask=ImageChops.subtract(landmask,lakes)
base.paste(SEA,mask=lakes)

# hills shading: SR_HR is equirectangular, 60 px per degree
sr=Image.open('SR_HR.tif')
x0=int((W0+180)*60); x1=int((E0+180)*60); y0=int((90-N0)*60); y1=int((90-S0)*60)
crop=sr.crop((x0,y0,x1,y1)).resize((Wpx,(y1-y0)*SC),Image.BICUBIC)
shade=Image.new('L',(Wpx,Hpx))
for row in range(Hpx):
    lat=math.degrees(2*math.atan(math.exp(my(N0)-row/ky))-math.pi/2)
    src=min(crop.height-1,max(0,int((N0-lat)*60*SC)))
    shade.paste(crop.crop((0,src,Wpx,src+1)),(0,row))
hist=shade.histogram(mask=landmask); tot=sum(hist); acc=0
for v,c in enumerate(hist):
    acc+=c
    if acc>tot*0.5: med=v; break
print('median shade',med)
lut=[max(0,min(255,int(255*(0.62+0.38*v/med)))) for v in range(256)]
sh=shade.point(lut).filter(ImageFilter.GaussianBlur(1))
shaded=ImageChops.multiply(base,Image.merge('RGB',(sh,sh,sh)))
out=Image.composite(shaded,base,landmask)
out.save(os.environ.get('OUT_JPG','israel4x.jpg'),quality=85,optimize=True)

# vectors, clipped loosely to the region, 4 decimals
def lines(path,keep=lambda p:True):
    res=[]
    for f in json.load(open(path,encoding='utf-8'))['features']:
        if not keep(f['properties']): continue
        g=f['geometry']; ls=[g['coordinates']] if g['type']=='LineString' else g['coordinates'] if g['type']=='MultiLineString' else []
        for l in ls:
            if any(W0<x<E0 and S0<y<N0 for x,y in l):
                res.append([[round(y,4),round(x,4)] for x,y in l])
    return res
data={
 'bounds':[[S0,W0],[N0,E0]],
 'rivers':lines('ne_10m_rivers_lake_centerlines.geojson'),
 'borders':lines('ne_10m_admin_0_boundary_lines_land.geojson'),
}
cities=[]
# Label priority when names would overlap (1 wins): Jerusalem and Tel Aviv, then Haifa, Beer Sheva and
# Eilat, then the other Israeli towns, then the neighbours' capitals, then the rest.
TOP={'Jerusalem':1,'Tel Aviv':1,'Haifa':2,'Beer Sheva':2,'Eilat':2}
for f in json.load(open('ne_10m_populated_places_simple.geojson',encoding='utf-8'))['features']:
    x,y=f['geometry']['coordinates']; p=f['properties']
    if W0<x<E0 and S0<y<N0 and (p['scalerank']<=7 or p['adm0name']=='Israel'):
        r=TOP.get(p['name'], 3 if p['adm0name']=='Israel' else 4 if p['adm0cap'] else 5)
        cities.append([p['name'],round(y,3),round(x,3),r])
extra=[('Eilat',29.557,34.952),('Ashdod',31.804,34.655),('Ashkelon',31.669,34.571),('Netanya',32.329,34.857),
       ('Hadera',32.434,34.919),('Tiberias',32.795,35.531),('Safed',32.965,35.496),('Nahariya',33.006,35.095),
       ('Kiryat Shmona',33.208,35.570),('Afula',32.608,35.289),('Dimona',31.069,35.033),('Mitzpe Ramon',30.610,34.801)]
names={c[0] for c in cities}
for n,y,x in extra:
    if n not in names: cities.append([n,y,x,TOP.get(n,3)])
data['cities']=cities
open('israel4x.js','w',encoding='utf-8').write('window.ISRAEL4X='+json.dumps(data,separators=(',',':'),ensure_ascii=False)+';')
import os
print('jpg',os.path.getsize('israel4x.jpg')//1024,'KB  js',os.path.getsize('israel4x.js')//1024,'KB  cities',len(cities),
      'rivers',len(data['rivers']),'borders',len(data['borders']))
