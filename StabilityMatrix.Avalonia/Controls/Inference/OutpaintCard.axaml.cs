using System;
using Avalonia.Controls.Primitives;
using Injectio.Attributes;
using StabilityMatrix.Core.Attributes;

namespace StabilityMatrix.Avalonia.Controls.Inference
{
    [RegisterTransient<OutpaintCard>]
    public partial class OutpaintCard : UserControlBase
    {
        public OutpaintCard()
        {
            Console.WriteLine("OutpaintCard VIEW LOADED");
            InitializeComponent();
        }
    }
}
