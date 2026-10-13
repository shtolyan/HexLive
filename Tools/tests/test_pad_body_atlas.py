import json
from pathlib import Path
import sys
import tempfile
import unittest
import numpy as np
from PIL import Image

sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from pad_body_atlas import pad

class AtlasPaddingTests(unittest.TestCase):
    def test_uv_edge_black_is_removed_but_dark_interior_detail_survives(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            image=np.zeros((64,64,3),np.uint8)
            image[17:47,17:47]=[190,140,110]
            image[30:34,30:34]=[4,2,1] # Genuine dark detail, not background.
            Image.fromarray(image).save(root/'source.png')
            (root/'mesh.json').write_text(json.dumps({'uv':[[.25,.25],[.75,.25],[.75,.75],[.25,.75]],'triangles':[0,1,2,0,2,3]}))
            pad(root/'source.png',root/'mesh.json',root/'out.png',gutter=4)
            result=np.array(Image.open(root/'out.png'))
            np.testing.assert_array_equal(result[30:34,30:34],image[30:34,30:34])
            self.assertGreater(int(result[16,32].mean()),100)
            self.assertGreater(int(result[14,32].mean()),100)
            np.testing.assert_array_equal(result[0,0],image[0,0])

    def test_gutter_does_not_wrap_across_texture_border(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory)
            image=np.zeros((64,64,3),np.uint8);image[20:44,:16]=[200,100,50]
            Image.fromarray(image).save(root/'source.png')
            (root/'mesh.json').write_text(json.dumps({'uv':[[0,.3125],[.25,.3125],[.25,.6875],[0,.6875]],'triangles':[0,1,2,0,2,3]}))
            pad(root/'source.png',root/'mesh.json',root/'out.png',gutter=4)
            result=np.array(Image.open(root/'out.png'))
            self.assertGreater(int(result[32,0].mean()),50)
            self.assertEqual(int(result[:,60:].max()),0)

if __name__=='__main__':unittest.main()
