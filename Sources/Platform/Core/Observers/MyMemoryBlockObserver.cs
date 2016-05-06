﻿using GoodAI.Core.Utils;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Reflection;
using GoodAI.Core.Memory;
using YAXLib;

namespace GoodAI.Core.Observers
{
    [YAXSerializeAs("MemoryBlockObserver")]
    public class MyMemoryBlockObserver : MyAbstractMemoryBlockObserver
    {
        #region Parameters

        public enum MyBoundPolicy
        {
            IHNERITED,
            MANUAL
        }

        public enum RenderingScale
        {
            Linear,
            InvTan
        }

        [YAXSerializableField]
        [MyBrowsable, Category("Texture"), DisplayName("Coloring Method")]
        public RenderingMethod Method
        {
            get { return m_method; }
            set
            {
                m_method = value;
                m_methodSelected = true;
                TriggerReset();
            }
        }
        private RenderingMethod m_method;

        private bool m_methodSelected;

        [YAXSerializableField]
        [MyBrowsable, Category("Scale + Bounds")]
        public RenderingScale Scale { get; set; }

        [YAXSerializableField(DefaultValue = MyBoundPolicy.MANUAL)]
        private MyBoundPolicy m_boundPolicy = MyBoundPolicy.IHNERITED;

        [MyBrowsable, Category("Scale + Bounds"), Description("The way the Min / Max of Y axis are chosen")]
        public MyBoundPolicy BoundPolicy
        {
            get { return m_boundPolicy; }

            set
            {
                m_boundPolicy = value;
                if (m_boundPolicy == MyBoundPolicy.IHNERITED)
                {
                    TriggerReset();
                }
            }
        }

        [YAXSerializableField(DefaultValue = 0)]
        private float m_minValue;

        [MyBrowsable, Category("Scale + Bounds"), DisplayName("M\tinValue")]
        public float MinValue
        {
            get { return m_minValue; }
            set
            {
                m_minValue = value;
                BoundPolicy = MyBoundPolicy.MANUAL;
            }
        }

        [YAXSerializableField(DefaultValue = 1)]
        private float m_maxValue;

        [MyBrowsable, Category("Scale + Bounds")]
        public float MaxValue
        {
            get { return m_maxValue; }
            set
            {
                m_maxValue = value;
                BoundPolicy = MyBoundPolicy.MANUAL;
            }
        }

        [YAXSerializableField(DefaultValue = 2)]
        [MyBrowsable, Category("Texture"), DisplayName("Vector Elements")]
        public int Elements
        {
            get { return m_elements; }
            set
            {
                if (value <= 0)
                    throw new ArgumentException("Vector element count must be greater than zero.");

                m_elements = value;
                TriggerReset();
            }
        }
        private int m_elements;

        [YAXSerializableField(DefaultValue = "")]
        [MyBrowsable, Category("Texture"), DisplayName("Custom Dimensions")]
        [Description("Comma separated dimensions, such as \"2, 3, *\".")]
        public string CustomDimensions
        {
            get
            {
                return m_customDimensions.PrintSource();
            }
            set
            {
                m_customDimensions = CustomDimensionsHint.Parse(value);
                TriggerReset();
            }
        }
        private CustomDimensionsHint m_customDimensions = CustomDimensionsHint.Empty;

        [YAXSerializableField(DefaultValue = 0)]
        [MyBrowsable, Category("Temporal")]
        public int TimeStep { get; set; }

        [YAXSerializableField]
        [MyBrowsable, Category("\tWindow")]
        public bool ShowCoordinates
        {
            get { return m_showCoordinates; }
            set
            {
                m_showCoordinatesSelected = true;
                m_showCoordinates = value;
            }
        }

        #endregion

        #region TensorObserverParameters

        private bool m_observeTensors;
        [YAXSerializableField(DefaultValue = false)]
        [MyBrowsable, Category("Tensor Observer"),
        Description("If enabled, memory block is displayed in tiles " +
            "(width and height of tile read from two first dimensions of MemoryBlock or CustomDimensions) " +
            "onto grid of width = TilesInRow.")]
        public bool ObserveTensors
        {
            get
            {
                return m_observeTensors;
            }
            set
            {
                m_observeTensors = value;
                TriggerReset();
            }
        }

        private bool m_useCustomDimensions;
        [YAXSerializableField(DefaultValue = false)]
        [MyBrowsable, Category("Tensor Observer"), Description("If enabled, the 'TileWidth, TileHeight,*' will be parsed from the CustomDimensions. From the MemoryBlock otherwise.")]
        public bool UseCustomDimensions
        {
            get
            {
                return m_useCustomDimensions;
            }
            set
            {
                m_useCustomDimensions = value;
                TriggerReset();
            }
        }

        private int m_tilesInRow;
        [YAXSerializableField(DefaultValue = 1)]
        [MyBrowsable, Category("Tensor Observer")]
        [Description("Number of tiles displayed in one row")]
        public int TilesInRow
        {
            get
            {
                return m_tilesInRow;
            }
            set
            {
                if (value > 0)
                {
                    m_tilesInRow = value;
                    TriggerReset();
                }
            }
        }

        private int m_tileWidth, m_tileHeight;

        #endregion

        private bool m_showCoordinatesSelected;
        private bool m_showCoordinates;

        private const short SmallVectorLimit = 10;

        protected MyCudaKernel m_vectorKernel;
        protected MyCudaKernel m_rgbKernel;
        protected MyCudaKernel m_tiledKernel, m_tiledRGBKernel;

        public MyMemoryBlockObserver()
        {
            BoundPolicy = MyBoundPolicy.IHNERITED;
            Elements = 2;
            MinValue = 0;
            MaxValue = 1;

            TargetChanged += MyMemoryBlockObserver_TargetChanged;
        }

        void MyMemoryBlockObserver_TargetChanged(object sender, PropertyChangedEventArgs e)
        {
            Type type = Target.GetType().GenericTypeArguments[0];
            m_kernel = MyKernelFactory.Instance.Kernel(@"Observers\ColorScaleObserver" + type.Name);
            m_vectorKernel = MyKernelFactory.Instance.Kernel(MyKernelFactory.Instance.DevCount - 1, @"Observers\ColorScaleObserverSingle", "DrawVectorsKernel");
            m_rgbKernel = MyKernelFactory.Instance.Kernel(MyKernelFactory.Instance.DevCount - 1, @"Observers\ColorScaleObserverSingle", "DrawRGBKernel");

            if (ObserveTensors && type.Name != "Single")
            {
                MyLog.WARNING.WriteLine("Observing tensors, with anything other than Signles not supported, will use Single");
            }
            if (Method == RenderingMethod.Vector || Method == RenderingMethod.RGB)
            {
                MyLog.WARNING.WriteLine("Observing tensors in RGB or Vector mode not supported");
            }
            m_tiledKernel    = MyKernelFactory.Instance.Kernel(MyKernelFactory.Instance.DevCount - 1, @"Observers\ColorScaleObserverSingle", "ColorScaleObserverTiledSingle");
            m_tiledRGBKernel = MyKernelFactory.Instance.Kernel(MyKernelFactory.Instance.DevCount - 1, @"Observers\ColorScaleObserverSingle", "DrawRGBTiledKernel");

            if (!m_methodSelected)
            {
                Method = Target.Metadata.GetOrDefault(MemoryBlockMetadataKeys.RenderingMethod,
                    defaultValue: RenderingMethod.RedGreenScale);
            }

            if (!m_showCoordinatesSelected)
            {
                ShowCoordinates = Target.Metadata.GetOrDefault(MemoryBlockMetadataKeys.ShowCoordinates,
                    defaultValue: false);
            }
        }

        protected override void Execute()
        {
            if (ObserveTensors)
            {
                if (Method == RenderingMethod.RGB)
                {
                    m_tiledRGBKernel.SetupExecution(TextureSize);
                    m_tiledRGBKernel.Run(Target.GetDevicePtr(ObserverGPU, 0, TimeStep), VBODevicePointer, TextureSize, m_tileWidth, m_tileHeight, TilesInRow);
                }
                else
                {
                    int pixelsInOrigImage = Target.Dims.ElementCount;
                    m_tiledKernel.SetupExecution(pixelsInOrigImage);
                    m_tiledKernel.Run(Target.GetDevicePtr(ObserverGPU, 0, TimeStep), (int)Method, (int)Scale, MinValue, MaxValue, VBODevicePointer, pixelsInOrigImage, m_tileWidth, m_tileHeight, TilesInRow);
                }
                return;
            }
            if (Method == RenderingMethod.Vector)
            {
                m_vectorKernel.SetupExecution(TextureSize);
                m_vectorKernel.Run(Target.GetDevicePtr(ObserverGPU, 0, TimeStep), Elements, MaxValue, VBODevicePointer, TextureSize);
            }
            else if (Method == RenderingMethod.RGB)
            {
                m_rgbKernel.SetupExecution(TextureSize);
                m_rgbKernel.Run(Target.GetDevicePtr(ObserverGPU, 0, TimeStep), VBODevicePointer, TextureSize);
            }
            else
            {
                m_kernel.SetupExecution(TextureSize);
                m_kernel.Run(Target.GetDevicePtr(ObserverGPU, 0, TimeStep), (int)Method, (int)Scale, MinValue, MaxValue, VBODevicePointer, TextureSize);
            }
        }

        private void ResetBounds()
        {
            if (BoundPolicy == MyBoundPolicy.IHNERITED)
            {
                if (!float.IsNegativeInfinity(Target.MinValueHint))
                    m_minValue = Target.MinValueHint;
                else
                    m_minValue = 0;
                if (!float.IsPositiveInfinity(Target.MaxValueHint))
                    m_maxValue = Target.MaxValueHint;
                else
                    m_maxValue = 1;
            }
        }

        protected override void Reset()
        {
            base.Reset();
            ResetBounds();

            SetTextureDimensions();
        }

        private TensorDimensions GetTileDimensions()
        {
            if (UseCustomDimensions)
            {
                TensorDimensions d;
                bool didApplyCustomDims = m_customDimensions.TryToApply(Target.Dims, out d);
                if (!didApplyCustomDims)
                {
                    MyLog.WARNING.WriteLine("Memory block '{0}: {1}' observer: {2}", Target.Owner.Name, Target.Name,
                        "Could not apply custom dimensions, will use default ones");
                    return Target.Dims;
                }
                return d;
            }
            return Target.Dims;
        }

        private void SetTextureDimensions()
        {
            string warning = "";
            Size textureSize = Size.Empty;

            if (ObserveTensors)
            {
                TensorDimensions d = GetTileDimensions();
                m_tileWidth = d[0];
                m_tileHeight = d[1];
                textureSize = ComputeTiledTextureSize(d, Target);
                if (Method == RenderingMethod.RGB)
                {
                    textureSize.Height /= 3;
                }
                else // add boundaries between tiles
                { 
                    int TilesInColumn = textureSize.Height * textureSize.Width / m_tileWidth / m_tileHeight / TilesInRow;
                    textureSize.Height += TilesInColumn-1;
                    textureSize.Width  += TilesInRow-1;
                }
                
            }
            else
            {
                textureSize = ComputeCustomTextureSize(Target.Dims, m_customDimensions, Method, Elements, out warning);
            }
            if (!string.IsNullOrEmpty(warning))
                MyLog.WARNING.WriteLine("Memory block '{0}: {1}' observer: {2}", Target.Owner.Name, Target.Name, warning);

            TextureWidth = textureSize.Width;
            TextureHeight = textureSize.Height;
        }

        private Size ComputeTiledTextureSize(TensorDimensions dims, MyAbstractMemoryBlock target)
        {
            if (m_tileWidth * m_tileHeight * TilesInRow > dims.ElementCount)
            {
                TilesInRow = dims.ElementCount / (m_tileWidth * m_tileHeight);
                MyLog.WARNING.WriteLine("Memory block '{0}: {1}' observer: {2}", Target.Owner.Name, Target.Name,
                    "TilesInRow too big, adjusting to the maximum value of " + TilesInRow);
            }

            int noBlocks = dims.ElementCount / (m_tileWidth * m_tileHeight);

            // in case the noBlocks is not divisible by TilesInRow, allocate enough space
            int height = (int)Math.Ceiling((decimal)noBlocks / TilesInRow);
            return new Size(m_tileWidth * TilesInRow, height * m_tileHeight);
        }

        // TODO(Premek): Report warnings using a logger interface.
        internal static Size ComputeCustomTextureSize(TensorDimensions dims, CustomDimensionsHint customDims,
            RenderingMethod method, int vectorElements, out string warning)
        {
            warning = "";

            if (dims.IsEmpty)
                return Size.Empty;

            bool isDivisible;
            string divisorName = (method == RenderingMethod.RGB) ? "3 (RGB channel count)" : "vector element count";

            bool isRowVector = (dims.Rank == 1) || (dims.Rank == 2 && dims[1] == 1);
            bool isColumnVector = !isRowVector && (dims.Rank == 2) && (dims[0] == 1);

            TensorDimensions adjustedDims;
            bool didApplyCustomDims = customDims.TryToApply(dims, out adjustedDims);
            if (!customDims.IsEmpty && !didApplyCustomDims)
                warning = "Could not apply custom dimensions (the element count must match the original).";

            if (!didApplyCustomDims && (isRowVector || isColumnVector))
            {
                return ComputeTextureSizeForVector(dims.ElementCount, isRowVector, method, vectorElements, divisorName,
                    ref warning);
            }

            int shrinkedLastDim = ShrinkSizeForRenderingMethod(adjustedDims[adjustedDims.Rank - 1], method,
                vectorElements, out isDivisible);

            if (!isDivisible || (shrinkedLastDim == 0))
            {
                if (string.IsNullOrEmpty(warning))
                    warning = string.Format("The last dimension is {0} {1}. Ignoring dimensions.",
                        (!isDivisible) ? "not divisible by" : "smaller than", divisorName);

                return ComputeTextureSize(
                    ShrinkSizeForRenderingMethod(dims.ElementCount, method, vectorElements, out isDivisible));
            }

            // Squash all dimensions except the first one together.
            // TODO(Premek): Decide according to actual sizes of the dimensions.
            int squashedOtherDims = shrinkedLastDim;
            for (int i = 1; i < adjustedDims.Rank - 1; i++)
                squashedOtherDims *= adjustedDims[i];

            return new Size(adjustedDims[0], squashedOtherDims);
        }

        private static Size ComputeTextureSizeForVector(int elementCount, bool isRowVector, RenderingMethod method,
            int vectorElements, string divisorName, ref string warning)
        {
            bool isDivisible;
            int shrinkedSize = ShrinkSizeForRenderingMethod(elementCount, method, vectorElements, out isDivisible);
            if (!isDivisible && string.IsNullOrEmpty(warning))
                warning = string.Format("Total count is not divisible by {0}.", divisorName);

            return elementCount <= SmallVectorLimit // Don't wrap small vectors.
                ? new Size(isRowVector ? shrinkedSize : 1, !isRowVector ? shrinkedSize : 1)
                : ComputeTextureSize(elementCount);
        }

        private static int ShrinkSizeForRenderingMethod(int size, RenderingMethod method, int vectorElements, out bool isDivisible)
        {
            int divisor = 1;

            // ReSharper disable once ConvertIfStatementToSwitchStatement
            if (method == RenderingMethod.RGB)
            {
                divisor = 3;
            }
            else if (method == RenderingMethod.Vector)
            {
                if (vectorElements < 1)
                    throw new ArgumentException("Vector element count must be greater then zero.", "vectorElements");

                divisor = vectorElements;
            }

            int result = size / divisor;

            isDivisible = (result * divisor == size);

            return result;
        }
    }
}
