using System.ComponentModel;
using System.Reflection;
using Elsa.Extensions;
using Elsa.Features.Attributes;
using Elsa.Features.Contracts;
using Elsa.Features.Models;
using Elsa.Features.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Elsa.Features.Implementations;

/// <inheritdoc />
public class Module : IModule
{
    private sealed record HostedServiceDescriptor(int Order, Type Type);

    private Dictionary<Type, IFeature> _features = new();
    private readonly HashSet<IFeature> _configuredFeatures = new();
    private readonly List<HostedServiceDescriptor> _hostedServiceDescriptors = new();

    /// <summary>
    /// Constructor.
    /// </summary>
    public Module(IServiceCollection services)
    {
        Services = services;
    }

    /// <inheritdoc />
    public IServiceCollection Services { get; }

    /// <inheritdoc />
    public IDictionary<object, object> Properties { get; } = new Dictionary<object, object>();

    /// <inheritdoc />
    public bool HasFeature<T>() where T : class, IFeature
    {
        return HasFeature(typeof(T));
    }

    /// <inheritdoc />
    public bool HasFeature(Type featureType)
    {
        return _features.ContainsKey(featureType);
    }

    /// <inheritdoc />
    public T Configure<T>(Action<T>? configure = null) where T : class, IFeature
    {
        return Configure(module => (T)Activator.CreateInstance(typeof(T), module)!, configure);
    }

    /// <inheritdoc />
    public T Configure<T>(Func<IModule, T> factory, Action<T>? configure = null) where T : class, IFeature
    {
        if (!_features.TryGetValue(typeof(T), out var feature))
        {
            feature = factory(this);
            _features[typeof(T)] = feature;
        }

        configure?.Invoke((T)feature);

        if (!_isApplying)
            return (T)feature;

        var dependencies = GetDependencyTypes(feature.GetType()).ToHashSet();
        foreach (var dependency in dependencies.Select(GetOrCreateFeature))
            ConfigureFeature(dependency);

        ConfigureFeature(feature);
        return (T)feature;
    }

    /// <inheritdoc />
    public IModule ConfigureHostedService<T>(int priority = 0) where T : class, IHostedService
    {
        return ConfigureHostedService(typeof(T), priority);
    }

    /// <inheritdoc />
    public IModule ConfigureHostedService(Type hostedServiceType, int priority = 0)
    {
        _hostedServiceDescriptors.Add(new(priority, hostedServiceType));
        return this;
    }

    private bool _isApplying;

    /// <inheritdoc />
    public void Apply()
    {
        _isApplying = true;
        var featureTypes = GetFeatureTypes();
        _features = featureTypes.ToDictionary(featureType => featureType, featureType => _features.TryGetValue(featureType, out var existingFeature) ? existingFeature : (IFeature)Activator.CreateInstance(featureType, this)!);

        // Iterate over a copy of the features to avoid concurrent modification exceptions.
        foreach (var feature in _features.Values.ToList())
        {
            // This will cause additional features to be added to _features.
            ConfigureFeature(feature);
        }

        // Filter out features that depend on other features that are not installed.
        _features = ExcludeFeaturesWithMissingDependencies(_features.Values).ToDictionary(x => x.GetType(), x => x);

        // Add hosted services in order of priority.
        foreach (var hostedServiceDescriptor in _hostedServiceDescriptors.OrderBy(x => x.Order))
            Services.TryAddEnumerable(ServiceDescriptor.Singleton(typeof(IHostedService), hostedServiceDescriptor.Type));

        // Make sure to use the complete list of features when applying them.
        foreach (var feature in _features.Values)
            feature.Apply();

        // Add a registry of enabled features to the service collection for client applications to reflect on what features are installed.
        var registry = new InstalledFeatureRegistry();
        foreach (var feature in _features.Values)
        {
            var type = feature.GetType();
            var name = type.Name.Replace("Feature", string.Empty);
            var ns = "Elsa";
            var displayName = type.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName ?? name;
            var description = type.GetCustomAttribute<DescriptionAttribute>()?.Description;
            registry.Add(new(name, ns, displayName, description));
        }

        Services.AddSingleton<IInstalledFeatureRegistry>(registry);
    }

    private IEnumerable<IFeature> ExcludeFeaturesWithMissingDependencies(IEnumerable<IFeature> features)
    {
        return
            from feature in features
            let featureType = feature.GetType()
            let dependencyOfAttributes = featureType.GetCustomAttributes<DependencyOfAttribute>(true).ToList()
            let missingDependencies = dependencyOfAttributes.Where(x => !_features.ContainsKey(x.Type)).ToList()
            where missingDependencies.Count == 0
            select feature;
    }

    private void ConfigureFeature(IFeature feature)
    {
        if (_configuredFeatures.Contains(feature))
            return;

        feature.Configure();
        feature.ConfigureHostedServices();
        _features[feature.GetType()] = feature;
        _configuredFeatures.Add(feature);
    }

    private IFeature GetOrCreateFeature(Type featureType)
    {
        return _features.TryGetValue(featureType, out var existingFeature) ? existingFeature : (IFeature)Activator.CreateInstance(featureType, this)!;
    }

    private HashSet<Type> GetFeatureTypes()
    {
        var featureTypes = _features.Keys.ToHashSet();
        var featureTypesWithDependencies = featureTypes.Concat(featureTypes.SelectMany(GetDependencyTypes)).ToHashSet();
        return featureTypesWithDependencies.TSort(x => x.GetCustomAttributes<DependsOnAttribute>(true).Select(dependsOn => dependsOn.Type)).ToHashSet();
    }

    // Recursively get dependency types.
    private IEnumerable<Type> GetDependencyTypes(Type type)
    {
        var dependencies = type.GetCustomAttributes<DependsOnAttribute>(true).Select(dependsOn => dependsOn.Type).ToList();
        return dependencies.Concat(dependencies.SelectMany(GetDependencyTypes));
    }
}