<a name="readme-top"></a>
<div align="center">
    <a href="https://github.com/liris-xr/Unity-Runtime-GUID">
        <picture>
            <source media="(prefers-color-scheme: dark)" srcset="/Documentation~/Images/logo_dark.png">
            <source media="(prefers-color-scheme: light)" srcset="/Documentation~/Images/logo_light.png">
            <img alt="logo" src="/Documentation~/Images/logo_light.png">
        </picture>
    </a>
    <p align="center">
        <a href="https://github.com/liris-xr/Unity-Runtime-GUID/issues">Report Bug</a>
        ·
        <a href="https://github.com/liris-xr/Unity-Runtime-GUID/issues">Request Feature</a>
    </p>
</div>

<details>
    <summary>Table of Contents</summary>
    <ol>
        <li><a href="#about-the-project">About The Project</a></li>
        <li><a href="#installation">Installation</a></li>
        <li><a href="#usage">Usage</a></li>
        <li><a href="#contributing">Contributing</a></li>
        <li><a href="#license">License</a></li>
        <li><a href="#contact">Contact</a></li>
    </ol>
</details>

## About

The UnityEngine uses a GUID system to uniquely identify objects across executions. However, this implementation is not accessible at runtime.
This package implements a custom GUID system to uniquely identify objects across executions without the need for any custom component attached to the objects.
The registries need to be updated in the editor before the application is built. New objects created at runtime will get a randomly generated GUID assigned to them.

## Installation

In the Unity Package Manager, click the `+` button and `Add package from git URL...` then enter the URL of this repository: `https://github.com/liris-xr/Unity-Runtime-GUID.git`.

## Usage

Example scripts are available in the [samples directory](Samples~/) of the project.

### Automatically update the registries

If you want GUID that are consistent across executions, you need to generate them before running your application through the Unity Editor.
To do so, from an editor script, update the registries using the `GuidRegistryUpdater`. For instance on pre-build or before entering play mode in the editor.

See an example [here](Samples~/CustomRegistryUpdater.cs).

### Access the GUIDs at runtime

You can access the GUIDs in the registries at runtime from anywhere in your application. If you try to get a GUID for an object not present in the registry, a new entry will be automatically generated with a random GUID.

See an example [here](Samples~/RuntimeGuidAccess.cs).

## Roadmap

See the [open issues](https://github.com/liris-xr/Unity-Runtime-GUID/issues) for a full list of proposed features (and
known issues).

## Contributing

Contributions are what make the open source community such an amazing place to learn, inspire, and create. Any
contributions you make are **greatly appreciated**.

If you have a suggestion that would make this better, please fork the repo and create a pull request. You can also
simply open an issue with the tag "enhancement".
Don't forget to give the project a star! Thanks again!

1. Fork the Project
2. Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3. Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the Branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## License

Distributed under the GNU General Public License v3.0. See `LICENSE.md` for more information.

## Contact

Charles JAVERLIAT - charles.javerliat@gmail.com