namespace Buckaroo
open FSharpx.Collections

module Solver =

  open FSharp.Control
  open Buckaroo.Result

  type LocatedAtom = Atom * PackageLocation

  type Constraints = Map<PackageIdentifier, Set<Constraint>>

  type Hints = AsyncSeq<LocatedAtom>

  type SolverState = {
    Solution : Solution;
    Constraints : Constraints;
    Depth : int;
    Visited : Set<PackageIdentifier * PackageLocation>;
    Locations : Map<AdhocPackageIdentifier, PackageSource>;
    Hints : AsyncSeq<LocatedAtom>;
  }

  type SearchStrategy = ISourceExplorer -> SolverState -> AsyncSeq<PackageIdentifier * VersionedLocation>

  let private withTimeout timeout action =
    async {
      let! child = Async.StartChild (action, timeout)
      return! child
    }

  let constraintsOf (ds: Set<Dependency>) =
    ds
    |> Set.toSeq
    |> Seq.map (fun x -> (x.Package, x.Constraint))
    |> Seq.groupBy fst
    |> Seq.map (fun (k, xs) -> (k, xs |> Seq.map snd |> Set.ofSeq))
    |> Map.ofSeq

  let rec versionSearchCost (v : Version) : int =
    match v with
    | Version.SemVer _-> 0
    | Version.Git(GitVersion.Tag _) -> 1
    | Version.Git(GitVersion.Branch _)  -> 2
    | Version.Git(GitVersion.Revision _)  -> 3

  let rec constraintSearchCost (c : Constraint) : int =
    match c with
    | Constraint.Exactly v -> versionSearchCost v
    | Constraint.Any xs ->
      xs |> Seq.map constraintSearchCost |> Seq.append [ 9 ] |> Seq.min
    | Constraint.All xs ->
      xs |> Seq.map constraintSearchCost |> Seq.append [ 0 ] |> Seq.max
    | Constraint.Complement _ -> 6

  let findUnsatisfied (solution : Solution) (dependencies : Constraints) = seq {
    yield! Set.difference
      (dependencies |> Map.keys |> Set.ofSeq)
      (solution.Resolutions |> Map.keys  |> Set.ofSeq)

    let maybeSatisfied =
      Set.intersect
        (dependencies |> Map.keys |> Set.ofSeq)
        (solution.Resolutions |> Map.keys  |> Set.ofSeq)

    yield!
      maybeSatisfied
      |> Set.toSeq
      |> Seq.map (fun package ->
        (package,
         Constraint.satisfiesSet
           (Constraint.All (dependencies.[package] |> Set.toList ))
           (fst solution.Resolutions.[package]).Versions ))
      |> Seq.filter(snd >> not)
      |> Seq.map fst
  }

  let private lockToHints (lock : Lock) =
    lock.Packages
    |> Map.toSeq
    |> Seq.map (fun (k, v) -> ({ Package = k; Version = v.Version }, v.Location))

  let private mergeLocations (a : Map<AdhocPackageIdentifier, PackageSource>) (b : Map<AdhocPackageIdentifier, PackageSource>) =
    let folder state next = result {
      let (key : AdhocPackageIdentifier, source) = next
      let! s = state
      match (s |> Map.tryFind key, source) with
      | Some (PackageSource.Http l), PackageSource.Http r ->
        let conflicts =
          l
          |> Map.toSeq
          |> Seq.map (fun (v, s) -> (v, s, r.[v]))
          |> Seq.filter(fun (_, sl, sr) -> sl <> sr)
          |> Seq.toList

        match (conflicts |> List.length > 0) with
        | false ->
          return!
            Result.Error
              (ConflictingLocations (key, PackageSource.Http l, PackageSource.Http r))
        | true ->
          return s
            |> Map.add
              key
              (PackageSource.Http (Map(Seq.concat [ (Map.toSeq l) ; (Map.toSeq r) ])))

      | Some (PackageSource.Git _), PackageSource.Git _ ->
        return
          s
          |> Map.add key source
      | Some a, b ->
        return! Result.Error
          (ConflictingLocations (key, a, b))
      | None, _->
        return
          s
          |> Map.add key source
    }

    a
      |> Map.toSeq
      |> Seq.fold folder (Result.Ok b)

  let quickSearchStrategy (sourceExplorer : ISourceExplorer) (state : SolverState) = asyncSeq {
    // Solution.visited will filter out any hints that we already tried in Solver.step
    for (atom, location) in state.Hints do
      yield (atom.Package, (location, Set [atom.Version]))

    let unsatisfied = findUnsatisfied state.Solution state.Constraints

    for package in unsatisfied do
      let acceptable =
        VersionedSource.getVersionSet
        >> (Constraint.satisfiesSet (Constraint.All (state.Constraints.[package] |> Set.toList)))
      for versionedSource in sourceExplorer.FetchVersions state.Locations package do
        if acceptable versionedSource then
          let! versionedLocation = sourceExplorer.FetchLocation versionedSource
          yield (package, versionedLocation)

  }

  let upgradeSearchStrategy (sourceExplorer : ISourceExplorer) (state : SolverState) = asyncSeq {
    let unsatisfied = findUnsatisfied state.Solution state.Constraints

    for package in unsatisfied do
      let acceptable =
        VersionedSource.getVersionSet
        >> (Constraint.satisfiesSet (Constraint.All (state.Constraints.[package] |> Set.toList)))
      for versionedSource in sourceExplorer.FetchVersions state.Locations package do
        if acceptable versionedSource then
          let! versionedLocation = sourceExplorer.FetchLocation versionedSource
          yield (package, versionedLocation)
  }

  let rec private step (sourceExplorer : ISourceExplorer) (strategy : SearchStrategy) (state : SolverState) : AsyncSeq<Resolution> = asyncSeq {

    let log (x : string) =
      "[" + (string state.Depth) + "] " + x
      |> System.Console.WriteLine

    if findUnsatisfied state.Solution state.Constraints |> Seq.isEmpty
      then
        yield Resolution.Ok state.Solution
      else
        let atomsToExplore =
          strategy sourceExplorer state
          |> AsyncSeq.filter (fun (package, (location, _)) ->
            Set.contains (package, location) state.Visited |> not)

        for (package, (location, versions)) in atomsToExplore do
          try
            log("Exploring " + (PackageIdentifier.show package) + " -> " + (PackageLocation.show location) + "...")

            // We pre-emptively grab the lock
            let! lockTask =
              sourceExplorer.FetchLock location
              |> Async.StartChild

            log("Fetching manifest... ")

            let! manifest = sourceExplorer.FetchManifest location

            log("Got manifest " + (string manifest))

            let! mergedLocations = async {
              return
                match mergeLocations state.Locations manifest.Locations with
                | Result.Ok xs -> xs
                | Result.Error e -> raise (new System.Exception(e.ToString()))
            }

            let resolvedVersion = {
              Versions = versions;
              Location = location;
              Manifest = manifest;
            }

            let freshHints =
              asyncSeq {
                try
                  let! lock = lockTask
                  yield!
                    lock
                    |> lockToHints
                    |> Seq.filter (fun (atom, location) ->
                      Set.contains (atom.Package, location) state.Visited |> not &&
                      state.Solution.Resolutions |> Map.containsKey atom.Package |> not
                    )
                    |> AsyncSeq.ofSeq
                with error ->
                  log("Could not fetch buckaroo.lock.toml for " + (PackageLocation.show location))
                  System.Console.WriteLine error
                  ()
              }

            let privatePackagesSolverState =
              {
                Solution = Solution.empty;
                Locations = Map.empty;
                Visited = Set.empty;
                Hints = state.Hints;
                Depth = state.Depth + 1;
                Constraints = constraintsOf manifest.PrivateDependencies
              }

            let privatePackagesSolutions =
              step sourceExplorer strategy privatePackagesSolverState
              |> AsyncSeq.choose (fun resolution ->
                match resolution with
                | Resolution.Ok solution -> Some solution
                | _ -> None
              )

            yield!
              privatePackagesSolutions
              |> AsyncSeq.collect (fun privatePackagesSolution -> asyncSeq {
                let nextState = {
                  state with
                    Solution =
                      {
                        state.Solution with
                          Resolutions =
                            state.Solution.Resolutions
                            |> Map.add package (resolvedVersion, privatePackagesSolution)
                      };
                    Constraints =
                      Map.union
                        state.Constraints
                        (constraintsOf manifest.Dependencies)

                    Depth = state.Depth + 1;
                    Visited =
                      state.Visited
                      |> Set.add (package, location);
                    Locations = mergedLocations;
                    Hints =
                      state.Hints
                      |> AsyncSeq.append freshHints;
                }

                yield! step sourceExplorer strategy nextState
              })
          with error ->
            log("Error exploring " + (PackageLocation.show location) + "...")
            System.Console.WriteLine(error)
            yield Resolution.Error error

        // We've run out of versions to try
    yield Resolution.Error (new System.Exception("No more versions to try! "))
  }

  let solutionCollector resolutions =
    resolutions
    |> AsyncSeq.take 2048
    |> AsyncSeq.filter (fun x ->
      match x with
      | Ok _ -> true
      | _ -> false
    )
    |> AsyncSeq.take 1
    |> AsyncSeq.toListAsync
    |> Async.RunSynchronously
    |> List.tryHead

  let solve (sourceExplorer : ISourceExplorer) (manifest : Manifest) (style : ResolutionStyle) (lock : Lock option) = async {
    let hints =
      lock
      |> Option.map (lockToHints >> AsyncSeq.ofSeq)
      |> Option.defaultValue AsyncSeq.empty

    let strategy =
      match style with
      | Quick -> quickSearchStrategy
      | Upgrading -> upgradeSearchStrategy

    let state = {
      Solution = Solution.empty;
      Constraints = Set.unionMany [manifest.Dependencies; manifest.PrivateDependencies]
        |> constraintsOf
      Depth = 0;
      Visited = Set.empty;
      Locations = manifest.Locations;
      Hints = hints;
    }

    let resolutions =
      step sourceExplorer strategy state

    return
      resolutions
      |> solutionCollector
      |> Option.defaultValue (Set.empty |> Resolution.Conflict)
  }
