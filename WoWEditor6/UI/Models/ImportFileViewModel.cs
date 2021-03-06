using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SharpDX.DXGI;
using WoWEditor6.IO;
using WoWEditor6.UI.Dialogs;
using WoWEditor6.Win32;
using Brushes = System.Windows.Media.Brushes;

namespace WoWEditor6.UI.Models
{
    enum ImportType
    {
        Texture,
        Raw,
        NotSupported
    }

    public class ImportFileViewModel
    {
        private readonly ImportFileDialog mDialog;

        public ImportFileViewModel()
        {
            mDialog = new ImportFileDialog(this);
        }

        public void ShowModal()
        {
            mDialog.ShowDialog();
        }

        public void Show()
        {
            mDialog.Show();
        }

        public void HandleFileImport()
        {
            var importType = IsFileSupported();
            var sourceName = mDialog.PathTextBox.Text;
            var targetName = mDialog.TargetNameBox.Text;

            if (importType == ImportType.Texture)
            {
                using (var img = Image.FromFile(sourceName) as Bitmap)
                {
                    if (img == null)
                        return;

                    using (var output = FileManager.Instance.GetOutputStream(targetName))
                    {
                        var texType = mDialog.TextureTypeBox.SelectedIndex;
                        var format = Format.BC1_UNorm;
                        var hasMips = true;
                        if (texType == 1)
                            format = Format.BC3_UNorm;
                        else if (texType == 2)
                        {
                            format = Format.BC2_UNorm;
                            hasMips = false;
                        }

                        IO.Files.Texture.BlpWriter.Write(output, img, format, hasMips);
                    }
                }
            }
            else
            {
                using (var output = FileManager.Instance.GetOutputStream(targetName))
                {
                    using (var input = File.OpenRead(sourceName))
                    {
                        input.CopyTo(output);
                    }
                }
            }

            mDialog.Close();
        }

        public void HandleFileImportSettings()
        {
            var importType = IsFileSupported();
            switch (importType)
            {
                case ImportType.NotSupported:
                    mDialog.PathErrorLabel.Text = "Sorry, this file cannot be imported";
                    mDialog.PathErrorLabel.Foreground = Brushes.Red;
                    return;

                case ImportType.Raw:
                    mDialog.PathErrorLabel.Text = "Info: File will be imported raw, no conversion";
                    mDialog.PathErrorLabel.Foreground = Brushes.Coral;
                    break;

                case ImportType.Texture:
                    mDialog.Height = 300;
                    mDialog.TextureSettingsPanel.Visibility = Visibility.Visible;
                    break;
            }

            mDialog.PathErrorLabel.Text = "";
        }

        public unsafe void BrowseForFile()
        {
            var dlg = (IFileOpenDialog)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("{DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7}")));
            var data = new IntPtr[4];
            data[0] = Marshal.StringToBSTR("Images (jpg, gif, png, bmp, exif, tiffs)");
            data[1] = Marshal.StringToBSTR("*.jpg;*.jpeg;*.gif;*.png;*.bmp;*.exif;*.tiff");
            data[2] = Marshal.StringToBSTR("All Files (*.*)");
            data[3] = Marshal.StringToBSTR("*.*");

            fixed (IntPtr* filters = data)
                dlg.SetFileTypes(2, new IntPtr(filters));

            for (var i = 0; i < 4; ++i) 
                Marshal.FreeBSTR(data[i]);

            if (dlg.Show(new WindowInteropHelper(mDialog).Handle) != 0)
                return;

            IShellItem item;
            dlg.GetResult(out item);
            if (item == null)
                return;

            var ptrOut = IntPtr.Zero;
            try
            {
                item.GetDisplayName(Sigdn.Filesyspath, out ptrOut);
                mDialog.PathTextBox.Text = Marshal.PtrToStringUni(ptrOut);
            }
            catch (Exception)
            {
                item.GetDisplayName(Sigdn.Normaldisplay, out ptrOut);
                mDialog.PathTextBox.Text = Marshal.PtrToStringUni(ptrOut);
            }
            finally
            {
                if (ptrOut != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(ptrOut);
            }

            mDialog.TargetNamePanel.Visibility = Visibility.Visible;
            mDialog.TargetNameBox.Text = Path.GetFileName(mDialog.PathTextBox.Text) ?? "";
            mDialog.Height = 200;
        }

        private ImportType IsFileSupported()
        {
            var file = mDialog.PathTextBox.Text;
            var extension = Path.GetExtension(file);
            if(string.IsNullOrEmpty(extension))
                return ImportType.NotSupported;

            extension = extension.ToLowerInvariant();

            var imageExtensions = new[]
            {
                ".jpg",
                ".jpeg",
                ".gif",
                ".exif",
                ".png",
                ".bmp"
            };

            return imageExtensions.Any(ext => string.Equals(ext, extension)) ? ImportType.Texture : ImportType.Raw;
        }
    }
}
