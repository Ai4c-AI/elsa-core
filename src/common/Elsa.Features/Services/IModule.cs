using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Elsa.Features.Services;

/// <summary>
/// A thin abstraction on top of <see cref="IServiceCollection"/> to help organize features and dependencies. 
/// </summary>
public interface IModule
{
    /// <summary>
    /// The service collection being populated.
    /// </summary>
    IServiceCollection Services { get; }

    /// <summary>
    /// A dictionary into which features can stash away values for later use. 
    /// </summary>
    IDictionary<object, object> Properties { get; }
    
    /// <summary>
    /// Returns true if a feature of the specified type has been configured.
    /// </summary>
    bool HasFeature<T>() where T : class, IFeature;
    
    /// <summary>
    /// Returns true if a feature of the specified type has been configured.
    /// </summary>
    bool HasFeature(Type featureType);

    /// <summary>
    /// Creates and configures a feature of the specified type.
    /// </summary>
    T Configure<T>(Action<T>? configure = null) where T : class, IFeature;

    /// <summary>
    /// Creates and configures a feature of the specified type.
    /// </summary>
    T Configure<T>(Func<IModule, T> factory, Action<T>? configure = null) where T : class, IFeature;

    /// <summary>
    /// Configures a <see cref="IHostedService"/> using an optional priority to control in which order it will be registered with the service container.
    /// </summary>
    IModule ConfigureHostedService<T>(int priority = 0) where T : class, IHostedService;

    /// <summary>
    /// Configures a <see cref="IHostedService"/> using an optional priority to control in which order it will be registered with the service container.
    /// </summary>
    IModule ConfigureHostedService(Type hostedServiceType, int priority = 0);

    /// <summary>
    /// Will apply all configured features, causing the <see cref="Services"/> collection to be populated. 
    /// </summary>
    void Apply();
}