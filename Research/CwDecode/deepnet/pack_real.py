"""Packs the real-recording Colab run (2026-10-19): cw_real.zip = code + practice data + his sessions + W1AW, all
ready-cut. The network to start from is NOT in it - it goes up on its own as cw_init.pt, so a new start point does
not mean uploading 600 MB again."""
import os
import zipfile

CD = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
os.chdir(CD)
z = zipfile.ZipFile('cw_real.zip', 'w', zipfile.ZIP_STORED)
for f in ['common.py', 'gen.py', 'model.py', 'train_gpu.py', 'finetune_gpu.py', 'noise_bank.npy', 'noise_kind.npy']:
    z.write(os.path.join('deepnet', f), 'deepnet/' + f)
for f in ['callsigns.txt', 'english.txt']:
    z.write(os.path.join('deepnet_pkg', f), 'deepnet/' + f)
for f in ['own_cut.npy', 'own_cut.json', 'w1aw_cut.npy', 'w1aw_cut.json']:
    z.write(os.path.join('build', f), 'data/' + f)
z.close()
print(os.path.getsize('cw_real.zip') // (1 << 20), 'MB')
