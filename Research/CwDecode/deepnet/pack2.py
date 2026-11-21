"""Packs the second Colab run: cw_train2.zip (code, the new noise bank, text lists, and the first run's best as init.pt)."""
import os
import zipfile

CD = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
os.chdir(CD)
z = zipfile.ZipFile('cw_train2.zip', 'w', zipfile.ZIP_DEFLATED)
for f in ['common.py', 'gen.py', 'model.py', 'train_gpu.py', 'noise_bank.npy', 'noise_kind.npy']:
    z.write(os.path.join('deepnet', f), 'deepnet/' + f)
for f in ['callsigns.txt', 'english.txt']:
    z.write(os.path.join('deepnet_pkg', f), 'deepnet/' + f)
z.write('deepnet/ckpt_colab40k.pt', 'deepnet/init.pt')
z.close()
print(os.path.getsize('cw_train2.zip') // 1024 // 1024, 'MB')
