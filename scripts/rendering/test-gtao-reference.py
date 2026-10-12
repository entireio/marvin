#!/usr/bin/env python3
"""Independent analytic checks for the ray reference used to reject AO errors."""
import importlib.util
from pathlib import Path
import unittest
import json
import tempfile
import numpy as np

spec=importlib.util.spec_from_file_location('fixture',Path(__file__).with_name('check-gtao-fixture.py'))
fixture=importlib.util.module_from_spec(spec);spec.loader.exec_module(fixture)

class ReferenceTests(unittest.TestCase):
    def test_finite_sheet_does_not_become_infinite_plane(self):
        directions=np.array([[0,0,-1],[.2,0,-1],[0,0,1],[1,0,0]],dtype=float)
        directions/=np.linalg.norm(directions,axis=1)[:,None]
        hits,indices=fixture.intersect(np.zeros(3),directions,fixture.planes('thin-occluder'))
        np.testing.assert_allclose(hits[:2],[3,4*np.sqrt(1.04)],rtol=1e-12)
        np.testing.assert_array_equal(indices,[1,0,-1,-1])
        self.assertTrue(np.isinf(hits[2:]).all())

    def test_mismatched_radius_and_stale_inputs_are_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary);case=root/'flat';result=case/'result';result.mkdir(parents=True)
            reference={'resolution':[64,64],'radius':1.6,'falloffFraction':0}
            (case/'reference.json').write_text(json.dumps(reference))
            compute=dict(reference,radius=2.5)
            (result/'compute.json').write_text(json.dumps(compute))
            with self.assertRaisesRegex(AssertionError,'radius mismatch'):fixture.check(root)
            compute.update(radius=1.6,inputSHA256={'preparation.json':'stale'})
            (result/'compute.json').write_text(json.dumps(compute));(case/'preparation.json').write_text('{}')
            with self.assertRaisesRegex(AssertionError,'stale input'):fixture.check(root)

    def test_isolated_plane_has_no_hemisphere_occlusion(self):
        value=fixture.reference(np.array([[0.,0.,-4.]]),np.array([[0.,0.,1.]]),fixture.planes('flat'),0)
        self.assertEqual(value.tolist(),[1.])

    def test_near_corner_blocks_half_hemisphere(self):
        value=fixture.reference(np.array([[0.,-1.199,-4.]]),np.array([[0.,0.,1.]]),fixture.planes('corner'),0)[0]
        self.assertGreater(value,.49);self.assertLess(value,.51)

if __name__=='__main__':unittest.main()
