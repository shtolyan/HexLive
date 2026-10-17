"""Install only a complete, hash-verified restore_people_skins.py staging result."""
import hashlib
import json
from pathlib import Path
import re
import shutil
import uuid

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'Build/RestoredPeopleSkins'
PEOPLE = ROOT / 'Assets/HexLiveContent/People'


def install():
    if not (SOURCE/'SUCCESS').exists():
        raise RuntimeError('Complete the Blender skin bake first')
    rows = json.loads((SOURCE/'receipt.json').read_text())
    expected = {('Marta', x) for x in ['Marta','Molly','Jana','Jolly']} | {('Kshishtof','Kshishtof')}
    if {(r['body'], r['skin']) for r in rows} != expected:
        raise RuntimeError('Incomplete authored skin set')
    for row in rows:
        if hashlib.sha256((SOURCE/row['output']).read_bytes()).hexdigest() != row['sha256']:
            raise RuntimeError('Stale skin output: '+row['output'])
        for item in row['sources'].values():
            item['material'] = item['material'].replace('\\', '/')
            item['texture'] = item['texture'].replace('\\', '/')
            if hashlib.sha256((ROOT/item['texture']).read_bytes()).hexdigest() != item['sha256']:
                raise RuntimeError('Authored source changed: '+item['texture'])
    aliases = {'Marta':dict(Marta='Marta', Molly='Molly', Jana='Jana', Jolly='Jolly', Masha='Marta', Rita='Molly'),
               'Kshishtof':dict(Kshishtof='Kshishtof', Tonny='Kshishtof')}
    template = (PEOPLE/'Source/Textures/Marta_BaseColor_Padded.png.meta').read_text()
    textures = {}
    for row in rows:
        path = PEOPLE/'Source/Textures'/row['output']
        meta = Path(str(path)+'.meta')
        guid = (re.search(r'^guid: (\w+)',meta.read_text(),re.M)[1] if meta.exists()
                else uuid.uuid5(uuid.NAMESPACE_URL, path.relative_to(ROOT).as_posix()).hex)
        shutil.copyfile(SOURCE/row['output'],path)
        if not meta.exists():
            meta.write_text(re.sub(r'^guid: \w+', 'guid: '+guid,template,flags=re.M),encoding='utf-8')
        textures[(row['body'],row['skin'])] = (guid, 'Textures/'+path.name)
    manifest_path = PEOPLE/'Source/source-manifest.json'
    manifest = json.loads(manifest_path.read_text())
    manifest['skinAtlases'] = {}
    for actor, mapping in aliases.items():
        manifest['skinAtlases'][actor] = {}
        for skin, donor in mapping.items():
            guid, relative = textures[(actor,donor)]
            manifest['skinAtlases'][actor][skin] = relative
            paths = [PEOPLE/'Materials'/f'Skin_{actor}_{skin}.mat']
            if skin == actor:
                paths.append(PEOPLE/'Materials'/f'{actor}_Atlas__Base.mat')
            for path in paths:
                text = path.read_text()
                for property_name in ('_BaseMap','_MainTex'):
                    text, count = re.subn(r'(- '+property_name+r':\s+m_Texture: \{fileID: 2800000, guid: )\w+',
                                          lambda m:m[1]+guid,text)
                    if count != 1:
                        raise RuntimeError('Unexpected material texture layout: '+str(path))
                for property_name in ('_BaseColor','_Color'):
                    text = re.sub(r'(- '+property_name+r': )\{[^}]+\}',r'\g<1>{r: 1, g: 1, b: 1, a: 1}',text)
                path.write_text(text,encoding='utf-8')
        material = manifest['materials'][actor+'_Atlas']
        material['baseColor'] = [1,1,1,1]
        for image in material['images']:
            if any(link['toSocket']=='Base Color' for link in image['links']):
                image['image'] = textures[(actor,actor)][1]
    manifest_path.write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    # Receipt records original assets and UV registration, not runtime publication.
    receipt_path = PEOPLE/'Source/restored-skins.json'
    receipt_path.write_text(json.dumps(rows,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    meta = Path(str(receipt_path)+'.meta')
    if not meta.exists():
        guid=uuid.uuid5(uuid.NAMESPACE_URL,receipt_path.relative_to(ROOT).as_posix()).hex
        meta.write_text('fileFormatVersion: 2\nguid: '+guid+'\nTextScriptImporter:\n  externalObjects: {}\n  userData: \n  assetBundleName: \n  assetBundleVariant: \n',encoding='utf-8')
    print('Installed 5 authored atlases, 8 skin IDs; no Unity import or bundle build.')


if __name__ == '__main__':
    install()
