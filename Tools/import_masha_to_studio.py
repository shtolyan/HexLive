#!/usr/bin/env python3
"""Import the existing local Masha profile without launching it or changing memory.

macOS migration helper. Never sources shell files or emits credentials. Uses the
same Keychain service/account convention as OperatingSystemSecretStore.
"""
import ctypes
import fcntl
import hashlib
import json
import os
from pathlib import Path
import shlex
import sys
import tempfile
import uuid


def environment(path):
    values = {}
    for raw in path.read_text().splitlines():
        line = raw.strip()
        if line.startswith('export '):
            line = line[7:]
        if not line or line.startswith('#') or '=' not in line:
            continue
        name, value = line.split('=', 1)
        parsed = shlex.split(value, comments=True)
        if len(parsed) != 1:
            raise ValueError('UnsupportedConfigAssignment')
        values[name] = parsed[0]
    return values


def store_credential(account, value):
    security = ctypes.CDLL('/System/Library/Frameworks/Security.framework/Security')
    core = ctypes.CDLL('/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation')
    pointer = ctypes.c_void_p
    uint = ctypes.c_uint32
    security.SecKeychainFindGenericPassword.argtypes = [pointer, uint, ctypes.c_char_p, uint, ctypes.c_char_p,
                                                      ctypes.POINTER(uint), ctypes.POINTER(pointer), ctypes.POINTER(pointer)]
    security.SecKeychainFindGenericPassword.restype = ctypes.c_int32
    security.SecKeychainAddGenericPassword.argtypes = [pointer, uint, ctypes.c_char_p, uint, ctypes.c_char_p, uint, pointer, ctypes.POINTER(pointer)]
    security.SecKeychainAddGenericPassword.restype = ctypes.c_int32
    security.SecKeychainItemFreeContent.argtypes = [pointer, pointer]
    core.CFRelease.argtypes = [pointer]
    service, name, secret = b'HexLive.AgentStudio', account.encode('ascii'), value.encode('utf-8')
    size, data, item = uint(), pointer(), pointer()
    status = security.SecKeychainFindGenericPassword(None, len(service), service, len(name), name,
                                                    ctypes.byref(size), ctypes.byref(data), ctypes.byref(item))
    if status == 0:
        try:
            if ctypes.string_at(data, size.value) != secret:
                raise ValueError('ExistingIntegrationCredentialConflict')
        finally:
            security.SecKeychainItemFreeContent(None, data)
            if item:
                core.CFRelease(item)
        return
    if status != -25300:
        raise ValueError('KeychainReadFailed')
    blob = ctypes.create_string_buffer(secret)
    try:
        status = security.SecKeychainAddGenericPassword(None, len(service), service, len(name), name,
                                                       len(secret), blob, ctypes.byref(item))
        if status != 0:
            raise ValueError('KeychainWriteFailed')
    finally:
        ctypes.memset(blob, 0, len(blob))
        if item:
            core.CFRelease(item)


def main():
    if sys.platform != 'darwin':
        raise ValueError('MacOSMigrationOnly')
    base = Path.home()
    env = environment(base / '.config/hexlive/agent-masha.env')
    workspace = Path(env.get('MASHA_HOME', str(base / 'Library/Application Support/Masha'))).resolve()
    if not (workspace / 'SOUL.md').is_file():
        raise ValueError('ExistingMashaWorkspaceRequired')
    identity = json.loads((workspace / '.state/state.json').read_text()).get('identity', {})
    if identity.get('id') != 'masha':
        raise ValueError('MashaIdentityNotConfirmed')
    backend_file = base / '.local/state/hexlive/agent-masha/llm-backend'
    backend = backend_file.read_text().strip() if backend_file.exists() else env.get('HEXLIVE_AGENT_LLM', 'xai')
    if backend not in ('codex', 'xai'):
        raise ValueError('UnsupportedLegacyBackend')
    root = base / 'Library/Application Support/HexLive/AgentStudio'
    root.mkdir(parents=True, exist_ok=True, mode=0o700)
    os.chmod(root, 0o700)
    path = root / 'profiles.json'
    with (root / '.profiles.lock').open('a+b') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        old = path.read_bytes() if path.exists() else None
        config = json.loads(old) if old else {'version': 1, 'agents': [], 'servers': []}
        if config.get('version') != 1:
            raise ValueError('UnsupportedStudioConfiguration')
        existing = next((p for p in config['agents'] if Path(p['workspace']).resolve() == workspace), None)
        if existing:
            print('Masha workspace is already registered; existing profile was preserved.')
            return
        if len(config['agents']) >= 64:
            raise ValueError('ProfileLimit')
        for variable, account in [('XAI_API_KEY', 'model.masha.grok'), ('ELEVENLABS_API_KEY', 'voice.masha.elevenlabs')]:
            if not env.get(variable):
                raise ValueError('MissingLegacyProviderCredential')
            store_credential(account, env[variable])
        profile = {
            'id': str(uuid.uuid5(uuid.NAMESPACE_URL, 'hexlive-agent-studio:' + str(workspace))),
            'name': identity.get('name') or 'Маша', 'workspace': str(workspace),
            'serverId': str(uuid.UUID(int=0)), 'worldId': '', 'npcId': 0,
            'model': {'provider': 4 if backend == 'codex' else 1,
                      'integrationId': 'codex' if backend == 'codex' else 'model.masha.grok',
                      'modelId': 'gpt-5.6-terra' if backend == 'codex' else env.get('HEXLIVE_AGENT_MODEL', 'grok-4.20-0309-reasoning'),
                      'reasoning': 'low' if backend == 'codex' else None},
            'voice': {'integrationId': 'voice.masha.elevenlabs',
                      'voiceId': env.get('HEXLIVE_MASHA_VOICE_ID', 'NsFK0aDGLbVusA7tQfOB'),
                      'modelId': env.get('HEXLIVE_TTS_MODEL', 'eleven_multilingual_v2')},
            'heartbeatSeconds': 30,
        }
        config['agents'].append(profile)
        if old:
            backup = root / ('profiles.before-masha-' + hashlib.sha256(old).hexdigest()[:16] + '.json')
            if not backup.exists():
                descriptor = os.open(backup, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
                with os.fdopen(descriptor, 'wb') as output:
                    output.write(old)
        descriptor, temporary = tempfile.mkstemp(prefix='.profiles-import-', dir=root)
        try:
            with os.fdopen(descriptor, 'w') as output:
                json.dump(config, output, ensure_ascii=False, indent=2)
                output.flush(); os.fsync(output.fileno())
            if (path.read_bytes() if path.exists() else None) != old:
                raise ValueError('ConfigurationChangedDuringImport')
            os.replace(temporary, path)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)
        print('Masha profile imported, stopped and unbound. Existing memory unchanged. Provider credentials stored in Keychain.')


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print('Import failed: ' + type(error).__name__, file=sys.stderr)
        sys.exit(1)
