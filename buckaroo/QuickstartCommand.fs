module Buckaroo.QuickstartCommand

open System
open System.Text.RegularExpressions

let private defaultBuck (libraryName : string) = 
  [
    "load('//:buckaroo_macros.bzl', 'buckaroo_deps')"; 
    ""; 
    "cxx_library("; 
    "  name = '" + libraryName + "', "; 
    "  header_namespace = '" + libraryName + "', "; 
    "  exported_headers = subdir_glob(["; 
    "    ('include', '**/*.hpp'), "; 
    "    ('include', '**/*.h'), "; 
    "  ]), "; 
    "  headers = subdir_glob(["; 
    "    ('private_include', '**/*.hpp'), "; 
    "    ('private_include', '**/*.h'), "; 
    "  ]), "; 
    "  srcs = glob(["; 
    "    'src/**/*.cpp', "; 
    "  ]), "; 
    "  deps = buckaroo_deps(), "; 
    "  visibility = ["; 
    "    'PUBLIC', "; 
    "  ], "; 
    ")"; 
    ""; 
    "cxx_binary("; 
    "  name = 'app', "; 
    "  srcs = ["; 
    "    'main.cpp', "; 
    "  ], "; 
    "  deps = ["; 
    "    '//:" + libraryName + "', "; 
    "  ], "; 
    ")"; 
    ""; 
  ]
  |> String.concat "\n"

let private defaultBuckconfig = 
  [
    "[project]"; 
    "  ignore = .git"; 
    ""; 
    "[cxx]"; 
    "  should_remap_host_platform = true"; 
    ""
  ]
  |> String.concat "\n"

let private defaultMain = 
  [
    "#include <iostream>"; 
    ""; 
    "int main() {"; 
    "  std::cout << \"Hello, world. \" << std::endl; "; 
    "";
    "  return 0; "; 
    "}";
    "";
  ]
  |> String.concat "\n"

let isValidProjectName (candidate : string) = 
  (new Regex(@"^[A-Za-z0-9\-_]{2,32}$")).IsMatch(candidate)

let requestProjectName = async {
  let mutable candidate = ""

  while isValidProjectName candidate |> not do 
    System.Console.WriteLine("Please enter a project name (alphanumeric, underscores, dashes): ")
    candidate <- System.Console.ReadLine().Trim()

  return candidate
}

let task (context : Tasks.TaskContext) = async {
  let! projectName = requestProjectName

  Console.WriteLine("Writing project files... ")

  do! Tasks.writeManifest Manifest.zero
  do! Files.mkdirp "src"
  do! Files.mkdirp "include"
  do! Files.mkdirp "private_include"
  do! Files.writeFile ".buckconfig" defaultBuckconfig
  do! Files.writeFile "BUCK" (defaultBuck projectName)
  do! Files.writeFile "main.cpp" defaultMain
  
  do! ResolveCommand.task context ResolutionStyle.Quick
  do! InstallCommand.task context

  Console.WriteLine("To start your app: ")
  Console.WriteLine("$ buck run :app")
  Console.WriteLine("")
}