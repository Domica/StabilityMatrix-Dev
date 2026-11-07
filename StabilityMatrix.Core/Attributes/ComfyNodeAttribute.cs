using System;

namespace StabilityMatrix.Core.Attributes;

/// <summary>
/// Marks a builder node with its corresponding ComfyUI class_type.
/// Used by the workflow builder to serialize nodes correctly.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ComfyNodeAttribute : Attribute
{
    public string ClassType { get; }

    public ComfyNodeAttribute(string classType)
    {
        ClassType = classType;
    }
}
