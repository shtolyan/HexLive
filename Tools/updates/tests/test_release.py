import base64
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('release', Path(__file__).resolve().parents[1] / 'release.py')
r = importlib.util.module_from_spec(spec); spec.loader.exec_module(r)

class ReleaseTests(unittest.TestCase):
    def test_versions_are_numeric(self):
        self.assertGreater(r.version('0.1.110'), r.version('0.1.99'))
        for bad in ('../1','1.2','1.2.3rc1'):
            with self.assertRaises(ValueError): r.version(bad)

    def test_atomic_preserves_previous_on_write_failure(self):
        with tempfile.TemporaryDirectory() as tmp:
            path=Path(tmp)/'latest.json';r.atomic(path,b'previous')
            with patch.object(r.os,'fsync',side_effect=OSError('disk full')):
                with self.assertRaises(OSError):r.atomic(path,b'new')
            self.assertEqual(path.read_bytes(),b'previous')

    def test_rsa_signature_roundtrip_and_tamper(self):
        from cryptography.hazmat.primitives import serialization,hashes
        from cryptography.hazmat.primitives.asymmetric import rsa,padding
        from cryptography.exceptions import InvalidSignature
        key=rsa.generate_private_key(public_exponent=65537,key_size=3072)
        manifest=dict(schemaVersion=1,product='client',channel='stable',platform='windows',architecture='x64',
            version='0.1.110',minimumVersion='0.1.99',sha256='a'*64,size=12,unpackedSize=20,driveFileId='file-id',publishedUtc='2026-09-13T00:00:00+00:00')
        payload=json.dumps(manifest).encode()
        envelope=dict(payload=base64.b64encode(payload).decode(),signature=base64.b64encode(key.sign(payload,padding.PKCS1v15(),hashes.SHA256())).decode())
        with tempfile.TemporaryDirectory() as tmp:
            pub=Path(tmp)/'public.pem';pub.write_bytes(key.public_key().public_bytes(serialization.Encoding.PEM,serialization.PublicFormat.SubjectPublicKeyInfo))
            self.assertEqual(r.verify(envelope,pub),manifest)
            envelope['payload']=base64.b64encode(payload+b' ').decode()
            with self.assertRaises(InvalidSignature):r.verify(envelope,pub)

    def test_bad_contract(self):
        with self.assertRaises(ValueError):r.version('../../escape')

if __name__ == '__main__': unittest.main()

class FakeResponse:
    def __init__(self, content=b'', data=None, status=200):
        self.content=content;self.data=data;self.status_code=status;self.headers={}
    def raise_for_status(self):
        if self.status_code >= 400:raise OSError('HTTP failure')
    def json(self):return self.data
    def iter_content(self, size):yield self.content

class SyncTests(unittest.TestCase):
    def test_corrupt_download_keeps_last_good_release(self):
        from types import SimpleNamespace
        from unittest.mock import Mock
        with tempfile.TemporaryDirectory() as tmp:
            cache=Path(tmp);target=cache/'windows'/'x64';target.mkdir(parents=True)
            (target/'latest.json').write_text('{"previous":true}')
            old={'version':'1.0.0','sha256':'a'*64}
            new={'version':'1.0.1','sha256':'b'*64,'size':3,'platform':'windows','architecture':'x64','driveFileId':'archive'}
            http=Mock();http.get.side_effect=[FakeResponse(b'{"new":true}',{'new':True}),FakeResponse(b'bad')]
            with patch.object(r,'files',return_value=[{'id':'latest'}]),patch.object(r,'verify',side_effect=[new,old]):
                with self.assertRaises(ValueError):r.sync_once(SimpleNamespace(cache=cache,folder='folder',public_key='public'),http)
            self.assertEqual((target/'latest.json').read_text(),'{"previous":true}')
            self.assertFalse((target/'blobs'/('b'*64)).exists())

    def test_completed_partial_is_verified_without_range_past_eof(self):
        from types import SimpleNamespace
        from unittest.mock import Mock
        import hashlib
        with tempfile.TemporaryDirectory() as tmp:
            cache=Path(tmp);target=cache/'windows'/'x64';(target/'blobs').mkdir(parents=True)
            sha=hashlib.sha256(b'zip').hexdigest();(target/'blobs'/(sha+'.part')).write_bytes(b'zip')
            new={'version':'1.0.1','sha256':sha,'size':3,'platform':'windows','architecture':'x64','driveFileId':'archive'}
            http=Mock();http.get.return_value=FakeResponse(b'{"new":true}',{'new':True})
            with patch.object(r,'files',side_effect=[[{'id':'latest'}],[],[]]),patch.object(r,'verify',return_value=new):
                r.sync_once(SimpleNamespace(cache=cache,folder='folder',public_key='public'),http)
            self.assertEqual(http.get.call_count,1)
            self.assertEqual((target/'blobs'/sha).read_bytes(),b'zip')
            self.assertTrue((target/'latest.json').exists())
