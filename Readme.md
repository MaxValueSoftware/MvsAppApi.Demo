# MvsAppApi.Demo

Example C# .Net Framework applications that demonstrate the use of the MvsAppApi packages.

There are 3 projects included in the MvsAppApi.Demo solution:

* ApiDemo - the Mvs App Api Demo program using the Json Adapter
* ApiDemo.Dll - the Mvs App Api Demo program using the Dll Adapter
* HelloWorld - a trivial API console app using the Json Adapter

The ApiDemo "GUI" applications includes a logging panel where you'll be able to see the message flow back and forth between the sample app and HM3 or PT4.  There's several tabs with other panels (hands, stats, queries, etc) that let you excercise a good portion of the API.
The HelloWorld example is trivial console application that attempts the initial connection.  (Note, currently broken since it doesn't register any server pipes).

## Setup to run Demo with HM3

1. Make sure you have an account setup on holdemmanager.com and HM3 is installed.  You can download the latest official beta from here ...
https://www.holdemmanager.com/download/index.php?product=HM3&channel=Beta

2. A purchased license is required to run apps.  Once you've purchased HM3, let us know your email address so we can add the demo app to your license.  We'll provide you with an app id and app name for your test app at the same time.

3. Open HM3 and login.  The API Demo app should get launched automatically and you'll see menus for it under the Apps menu.

4. Once you've explored the API Demo, have a look at the latest API documentation here too to assess whether you are ready to start developing your own application.
https://docs.google.com/document/d/1uJyj3xzq8HvRuqOkU7dAVNy_t3JYBb6x6bYdtXzsocM/edit

Note, the API documentation isn't strictly necessary when using the .Net packages for app development but its still useful to review.  It helps you to better understand the packages since they're implemented using the API and handle all the underlying details (such as creating client+server pipe connections, building/parsing json requests/methods, etc) automatically for you.

 
## Setup to build and run the API demo and your test app from Visual Studio

1. Run Visual Studio 2019 or newer as administrator (this is necessary for pipe communications)

2. Open the MvsAppApi.Demo solution

3. Before you build, you need to configure a package source that points to the following github package repository:

* https://nuget.pkg.github.com/MaxValueSoftware/index.json

* You'll need an account on Github and a personal-access-token (PAT) with read access.  These credentials should be entered manually for the package source in nuget.config as described here:

* https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-nuget-registry#authenticating-with-a-personal-access-token

4. Edit DemoUI\ViewModels\MainWindowViewModel.cs and insert your test app's AppId and AppName (at or around line 64).  

* Note, there's also --appId and --appName command line parameters that you can 
use to override the hard-coded values when running the app manually.  Either way, the hard-coded values will be needed later when you're ready to upload your app to the Max Value Software servers since 
these parameters won't be known or supplied by HM3 when it launches your app for users.

5. Set ApiDemo as the start project (and ignore ApiDemo.dll and Helloworld projects for now)

6. If you're using PT4, you'll need to use --tracker=pt4 as a command line switch (hm3 is the default)

7. With the tracker running, build and run the demo (as your test app) and you should see that its added to the App menu

8. You can then make a copy of the demo solution as the basis for your test app or you could create your test app from an empty project.  Since you'll be sharing the same app id and app name 
for both the demo and your own test app, only one of them can be run at a time from Visual Studio.

