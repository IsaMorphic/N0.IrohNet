use std::error::Error;

fn main() -> Result<(), Box<dyn Error>> {
    // using bindgen, generate binding code
   bindgen::Builder::default()
        .header("../iroh-c-ffi/irohnet.h")
        .generate()?
        .write_to_file("src/iroh.rs")?;
        
    // csbindgen code, generate C# dll import
    csbindgen::Builder::default()
        .input_bindgen_file("src/iroh.rs")
        .rust_file_header("extern crate iroh_c_ffi;\nuse super::iroh::*;")
        .rust_method_prefix("iroh_")
        .csharp_entry_point_prefix("iroh_")
        .csharp_dll_name("iroh")
        .csharp_namespace("N0.IrohNet")
        .csharp_class_accessibility("public")
        .csharp_class_name("iroh")
        .generate_to_file("src/iroh_ffi.rs", "../NativeMethods.g.cs")
        .unwrap();

    Ok(())
}