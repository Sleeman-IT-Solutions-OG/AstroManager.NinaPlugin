using System.Reflection;
using System.Runtime.InteropServices;

// [MANDATORY] The following GUID is used as a unique identifier of the plugin
[assembly: Guid("B8E5A3F2-1C4D-4E6B-9F8A-2D3E4F5A6B7C")]

// [MANDATORY] The assembly versioning
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

// [MANDATORY] The name of your plugin
[assembly: AssemblyTitle("AstroManager")]

// [MANDATORY] A short description of your plugin
[assembly: AssemblyDescription("Use AstroManager with NINA Advanced Sequencer. Documentation: https://docs.astro.sleeman.at")]

// Your name
[assembly: AssemblyCompany("Michael Sleeman")]

// The product name that this plugin is part of
[assembly: AssemblyProduct("AstroManager")]

[assembly: AssemblyCopyright("Copyright © 2025")]

// The minimum Version of N.I.N.A. that this plugin is compatible with
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.2.0.9001")]

// The license your plugin code is using
[assembly: AssemblyMetadata("License", "MIT")]
[assembly: AssemblyMetadata("LicenseURL", "https://opensource.org/licenses/MIT")]
[assembly: AssemblyMetadata("DocumentationURL", "https://docs.astro.sleeman.at")]

// Optional attributes
[assembly: AssemblyMetadata("Tags", "AstroManager,Scheduler,Targets,Imaging")]
[assembly: AssemblyMetadata("LongDescription", @"Documentation: https://docs.astro.sleeman.at

AstroManager enables NINA to fetch scheduled targets, imaging goals, and session data from AstroManager's API. 

It dynamically generates sequence instructions based on your imaging plan including:
- Slew to target coordinates
- Plate solve and center
- Filter switching
- Exposure sequences

Requirements:
- Valid AstroManager license (request within AstroManager app)")]

// COM visibility
[assembly: ComVisible(false)]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
