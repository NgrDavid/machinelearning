using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Xml.Serialization;
using static TorchSharp.torch;

namespace Bonsai.ML.Torch
{
    /// <summary>
    /// Moves the input tensor to the specified device.
    /// </summary>
    [Combinator]
    [ResetCombinator]
    [Description("Moves the input tensor to the specified device.")]
    [WorkflowElementCategory(ElementCategory.Transform)]
    public class ToDevice
    {
        /// <summary>
        /// The device to which the input tensor should be moved.
        /// </summary>
        [XmlIgnore]
        [Description("The device to which the input tensor should be moved.")]
        public Device Device { get; set; }

        /// <summary>
        /// Returns the input tensor moved to the specified device.
        /// </summary>
        /// <param name="source"></param>
        /// <returns></returns>
        public IObservable<Tensor> Process(IObservable<Tensor> source)
        {
            return source.Select(tensor => tensor.to(Device));
        }
    }
}