extern crate bindgen;

fn main() {
    let builder = 
    if std::env::var_os("CARGO_FEATURE_ANDROID").is_some() 
    {
        bindgen::Builder::default()
        .clang_arg("--sysroot=/usr/local/share/android-ndk/sysroot")
    } else { bindgen::Builder::default() };
    // The input header we would like to generate
    // bindings for.
    builder.header("include/irohnet.h")
    // Tell cargo to invalidate the built crate whenever any of the
    // included header files changed.
    .parse_callbacks(Box::new(bindgen::CargoCallbacks::new()))
    // Finish the builder and generate the bindings.
    .generate()
    // Unwrap the Result and panic on failure.
    .expect("Unable to generate bindings")
    .write_to_file("src/iroh.rs")
    .expect("Couldn't write bindings!");


    // csbindgen code, generate both rust ffi and C# dll import
    csbindgen::Builder::default()
    .input_bindgen_file("src/iroh.rs") // read from bindgen generated code
    .csharp_dll_name("iroh")
    .csharp_namespace("IrohNet")
    .generate_csharp_file("../NativeMethods.g.cs")
    .unwrap();
}