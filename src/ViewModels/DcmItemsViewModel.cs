﻿namespace SimpleDICOMToolkit.ViewModels
{
    using Dicom;
    using Stylet;
    using StyletIoC;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Windows;
    using Logging;
    using Models;
    using Services;

    public class DcmItemsViewModel : Screen, IHandle<UpdateDicomElementItem>
    {
        [Inject]
        private IWindowManager _windowManager;

        [Inject(Key = "filelogger")]
        private ILoggerService _logger;

        [Inject]
        private IViewModelFactory _viewModelFactory;

        [Inject]
        private IDialogServiceEx _dialogService;

        private readonly IEventAggregator _eventAggregator;

        private DicomFile _currentFile;

        public BindableCollection<DcmItem> DicomItems { get; private set; } = new BindableCollection<DcmItem>();

        private DcmItem _currentItem;

        public DcmItemsViewModel(IEventAggregator eventAggregator)
        {
            DisplayName = "DICOM Dump";
            _eventAggregator = eventAggregator;
            _eventAggregator.Subscribe(this);
        }

        protected override void OnClose()
        {
            _eventAggregator.Unsubscribe(this);

            base.OnClose();
        }

        public void Handle(UpdateDicomElementItem message)
        {
            AddOrUpdateDicomItem(message.Dataset, message.VR, message.Tag, message.Values);
        }

        public void OnDragFileOver(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effects = DragDropEffects.Link;
            else
                e.Effects = DragDropEffects.None;

            e.Handled = true;
        }

        public async void OnDropFile(DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            string path = ((System.Array)e.Data.GetData(DataFormats.FileDrop)).GetValue(0).ToString();

            if (!File.Exists(path))
                return;

            await OpenDcmFile(path);
        }

        public async Task OpenDcmFile(string file)
        {
            if (!DicomFile.HasValidHeader(file))
            {
                _logger.Warn("{0} is not a valid DICOM file.", file);
                return;
            }

            DicomItems.Clear();

            _currentFile = await DicomFile.OpenAsync(file);
            _currentFile.Dataset.NotValidated();

            var enumerator = _currentFile.FileMetaInfo.GetEnumerator();

            while (enumerator.MoveNext())
            {
                DicomItems.Add(new DcmItem(enumerator.Current, _currentFile.FileMetaInfo));
            }

            enumerator = _currentFile.Dataset.GetEnumerator();

            while (enumerator.MoveNext())
            {
                DicomItems.Add(new DcmItem(enumerator.Current, _currentFile.Dataset));
            }
        }

        public void DcmItemTapped(DcmItem item)
        {
            if (item.TagType != DcmTagType.Tag)
                return;

            if (item.DcmTag == DicomTag.PixelData)
            {
                ShowDcmImage(item);
            }
            else
            {
                EditDicomItem(item);
            }
        }

        public void ShowDcmImage(DcmItem item)
        {
            var preview = _viewModelFactory.GetPreviewImageViewModel();
            preview.Initialize(item.Dataset);

            _windowManager.ShowDialog(preview, this);
        }

        public void EditDicomItem(DcmItem item)
        {
            _currentItem = item;

            var editor = _viewModelFactory.GetEditDicomItemViewModel();
            editor.Initialize(item.Dataset, item.DcmTag);

            _windowManager.ShowDialog(editor, this);
        }

        public void RemoveDicomItem(DcmItem item)
        {
            // Remove from view
            var col = GetParentCollection(item);
            if (col != null)
            {
                col.Remove(item);
            }
            // Remove from dataset
            if (item.Dataset != null)
            {
                if (item.Sequence != null && item.TagType == DcmTagType.SequenceItem) // is Sequence Item
                {
                    item.Sequence.Items.Remove(item.Dataset);
                }
                else // is DicomTag or DicomSequence
                {
                    item.Dataset.Remove(item.DcmTag);
                }
            }
            else
            {
                _logger.Warn("Cannot remove {0} from dataset, dataset is null.", item.DcmTag);
            }
        }

        private BindableCollection<DcmItem> GetParentCollection(DcmItem item)
        {
            if (DicomItems.Contains(item))
            {
                return DicomItems;
            }

            return GetParentCollection(item, DicomItems.Where(x => x.SequenceItems != null));
        }

        private BindableCollection<DcmItem> GetParentCollection(DcmItem item, IEnumerable<DcmItem> sequence)
        {
            foreach (var seq in sequence)
            {
                if (seq.SequenceItems.Contains(item))
                {
                    return seq.SequenceItems;
                }

                var temp = GetParentCollection(item, seq.SequenceItems.Where(x => x.SequenceItems != null));

                if (temp != null)
                {
                    return temp;
                }
            }

            return null;
        }

        //private DicomDataset GetItemDataset(DcmItem item)
        //{
        //    if (_currentFile.FileMetaInfo.Contains(item.DcmTag))
        //        return _currentFile.FileMetaInfo;

        //    if (_currentFile.Dataset.Contains(item.DcmTag))
        //        return _currentFile.Dataset;

        //    return GetItemDataset(item, _currentFile.Dataset.Where(x => x is DicomSequence));
        //}

        //private DicomDataset GetItemDataset(DcmItem item, IEnumerable<DicomItem> sequence)
        //{
        //    foreach (var seq in sequence)
        //    {
        //        foreach (var dataset in (seq as DicomSequence).Items)
        //        {
        //            if (dataset.Contains(item.DcmTag))
        //            {
        //                return dataset;
        //            }

        //            var temp = GetItemDataset(item, dataset.Where(x => x is DicomSequence));

        //            if (temp != null)
        //            {
        //                return temp;
        //            }
        //        }
        //    }

        //    return null;
        //}

        public void SaveNewDicom()
        {
            _dialogService.ShowSaveFileDialog("DICOM Image (*.dcm;*.dic)|*.dcm;*.dic", null,
                async (result, path) =>
                {
                    if (result == true)
                    {
                        await _currentFile.SaveAsync(path);
                    }
                });
        }

        private void AddOrUpdateDicomItem(DicomDataset dataset, DicomVR vr, DicomTag tag, string[] values)
        {
            string charset = _currentFile.Dataset.GetSingleValueOrDefault(DicomTag.SpecificCharacterSet, "ISO_IR 192");

            try
            {
                if (vr == DicomVR.OB || vr == DicomVR.UN)
                {
                    byte[] temp = new byte[values.Length];
                    for (int i = 0; i < values.Length; i++)
                    {
                        temp[i] = byte.Parse(values[i]);
                    }
                    dataset.AddOrUpdate(vr, tag, DicomEncoding.GetEncoding(charset), temp);
                }
                else
                {
                    dataset.AddOrUpdate(vr, tag, DicomEncoding.GetEncoding(charset), values);
                }
            }
            catch (System.Exception e)
            {
                _logger.Error(e);
                return;
            }

            _currentItem.UpdateItem(dataset.GetDicomItem<DicomElement>(tag));
        }
    }
}
