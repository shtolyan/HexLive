"""Recover separate Daz cutout maps from original material GUIDs, not filenames."""
import argparse
import hashlib
import json
import re
from pathlib import Path


def apply_source_cutoff(project, config, material):
    text = (project/config['file']).read_text(encoding='utf-8-sig')
    match = re.search(r'^    - _Cutoff: ([0-9.eE+-]+)$', text, re.M)
    if config.get('alphaCutoff') is not None:
        material['alphaCutoff'] = config['alphaCutoff']
    elif match and 0 < float(match[1]) < 1:
        material['alphaCutoff'] = float(match[1])
    if match: material['originalCutoff'] = float(match[1])


def add_opacity_source(project, output, config, material, textures, key):
    source = project/config['file']
    text = source.read_text(encoding='utf-8-sig')
    match = re.search(r'    - _CutoutOpacityMap:\s*\n\s*m_Texture: \{fileID: 2800000, guid: ([0-9a-f]{32}), type: 3\}\s*\n\s*m_Scale: \{x: ([^,]+), y: ([^}]+)\}\s*\n\s*m_Offset: \{x: ([^,]+), y: ([^}]+)\}', text)
    if not match:
        return False
    candidates = [p.with_suffix('') for p in source.parent.glob('*.meta')
                  if re.search(r'^guid: '+match[1]+r'$', p.read_text(encoding='utf-8-sig'), re.M)]
    if len(candidates) != 1:
        raise ValueError('Cannot uniquely resolve opacity GUID for '+str(source))
    mask = candidates[0]; data = mask.read_bytes(); digest = hashlib.sha256(data).hexdigest()
    relative = 'Textures/hair_opacity_'+digest[:20]+mask.suffix.lower()
    target = output/relative; target.parent.mkdir(parents=True,exist_ok=True)
    if target.exists() and target.read_bytes()!=data:
        raise ValueError('Conflicting opacity source '+str(target))
    target.write_bytes(data)
    material['opacitySource'] = {'image':relative, 'channel':'r',
        'uvScale':[float(match[2]),float(match[3])], 'uvOffset':[float(match[4]),float(match[5])],
        'originalMaterial':config['file'], 'originalGuid':match[1]}
    # Cutout coverage comes from the authored mask, not a legacy tint alpha
    # (the old LowPonytail cap had alpha=0 with cutoff=0).
    material['baseColor'][3] = 1
    if key.startswith('LowPonytail_SourceUV_') and config['name'].lower() not in ('cap','scalp','base'):
        # This mask has a low signal range; .42 erases almost every strand.
        material['alphaCutoff'] = .08
    textures[key+'_Opacity']={'path':relative,'sha256':digest,
        'sourcePath':str(mask.relative_to(project)).replace('\\','/'),'colorSpace':'Linear'}
    return True


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--project',type=Path,required=True)
    parser.add_argument('--inputs',type=Path,required=True)
    parser.add_argument('--manifest',type=Path,required=True)
    args=parser.parse_args();project=args.project.resolve()
    original=args.manifest.read_bytes(); manifest=json.loads(original)
    selected=json.loads(args.inputs.read_text());configs={}
    for model in selected['models']:
        if model['lod']==0:
            configs[model['style']]=json.loads((args.inputs.parent/(model['candidate']+'.json')).read_text())['materials']
    count=0
    for key,material in manifest['materials'].items():
        if material.get('authoredHair') is not True:continue
        style,slot=key.rsplit('_SourceUV_',1)
        apply_source_cutoff(project,configs[style][int(slot)],material)
        count+=add_opacity_source(project,args.manifest.parent,configs[style][int(slot)],material,manifest['textures'],key)
    assert args.manifest.read_bytes()==original,'Concurrent manifest change'
    args.manifest.write_text(json.dumps(manifest,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'materialSlotsWithSeparateOpacity':count}))


if __name__=='__main__':main()
